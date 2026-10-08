using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Reporting;
using CrewCall.Messaging;
using RabbitMQ.Client;
using Xunit;
using static CrewCall.Reporting.Tests.Events;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// The reporting consumer against a real RabbitMQ (ADR-0016): crewcall.events → crewcall.reporting → projections →
/// Reporting API, duplicate deliveries, and catching up after the reporting service was down.
/// </summary>
[Collection(Sequential.Name)]
public sealed class ReportingRabbitMqTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private readonly string _queue = ReportingHosts.NewQueueName();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public async ValueTask DisposeAsync() => await ReportingHosts.DeleteQueueAsync(infrastructure, _queue);

    private static Task<TechnicianActivityReport> ReportWhenAsync(HttpClient client, Guid technicianId, Func<TechnicianActivityReport, bool> done) =>
        Wait.UntilAsync(
            async () => (await client.GetFromJsonAsync<TechnicianActivityReport>(
                $"/api/reporting/technicians/{technicianId}?start=2038-06-01&end=2038-06-30", Cancellation))!,
            done, "the projected report");

    private static async Task WaitForQueueAsync(RabbitMqConnection broker, string queue)
    {
        await Wait.UntilAsync(async () =>
        {
            try
            {
                await using var probe = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
                return (await probe.QueueDeclarePassiveAsync(queue, Cancellation)).ConsumerCount > 0;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                return false;
            }
        }, ready => ready, "the reporting consumer");
    }

    [Fact]
    public async Task Messages_published_to_crewcall_events_reach_the_reporting_queue_consumer_database_and_api_once()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue);
        using var client = host.CreateClient();
        await using var broker = ReportingHosts.Broker(infrastructure);
        await WaitForQueueAsync(broker, _queue);
        var (visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        var completion = Envelope(WorkCompleted(visitId, Day1.AddHours(12), 15m, 60m, 5m));

        // A duplicate delivery of the completion is part of the stream.
        await ReportingHosts.PublishAsync(broker,
        [
            Envelope(VisitCreated(visitId, Day1.AddHours(10), TimeSpan.FromHours(1))),
            completion,
            Envelope(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(8))),
            completion
        ]);

        var report = await ReportWhenAsync(client, technicianId, r => r.CompletedVisits == 1 && r.AssignmentCount == 1);
        Assert.Equal((15m, 60m, 5m, 55m), (report.TravelMinutes, report.GrossWorkMinutes, report.PauseMinutes, report.NetWorkMinutes));

        // Every delivery settled (ACKed after its commit), the duplicate included, and the counts did not move.
        await using var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
        await Wait.UntilAsync(async () => (await channel.QueueDeclarePassiveAsync(_queue, Cancellation)).MessageCount, depth => depth == 0, "an empty queue");
        await Task.Delay(500, Cancellation);
        Assert.Equal(1, (await ReportWhenAsync(client, technicianId, _ => true)).CompletedVisits);

        using var health = await client.GetAsync("/health", Cancellation);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task While_the_reporting_service_is_down_messages_wait_in_its_durable_queue_and_it_catches_up_when_it_returns()
    {
        await using var broker = ReportingHosts.Broker(infrastructure);
        await using (var first = ReportingHosts.Reporting(infrastructure, _queue))
        {
            _ = first.Services; // starts the consumer, which declares and binds the durable queue
            await WaitForQueueAsync(broker, _queue);
        }

        // Reporting is down. The operational side keeps publishing; nothing is lost.
        var (visitId, technicianId) = (Guid.NewGuid(), Guid.NewGuid());
        await ReportingHosts.PublishAsync(broker,
        [
            Envelope(VisitCreated(visitId, Day1.AddHours(10), TimeSpan.FromHours(1))),
            Envelope(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, Day1.AddHours(8))),
            Envelope(WorkCompleted(visitId, Day1.AddHours(11), null, 45m, 0m))
        ]);
        await using (var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation))
        {
            Assert.Equal(3u, (await channel.QueueDeclarePassiveAsync(_queue, Cancellation)).MessageCount);
        }

        // Reporting returns and projects the backlog.
        await using var second = ReportingHosts.Reporting(infrastructure, _queue);
        using var client = second.CreateClient();
        var report = await ReportWhenAsync(client, technicianId, r => r.CompletedVisits == 1);
        Assert.Equal((1, 45m), (report.AssignmentCount, report.NetWorkMinutes));
    }
}
