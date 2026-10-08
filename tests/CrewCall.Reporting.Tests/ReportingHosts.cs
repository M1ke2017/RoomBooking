extern alias reporting;

using CrewCall.Contracts.Integration;
using CrewCall.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Xunit;

namespace CrewCall.Reporting.Tests;

internal static class ReportingHosts
{
    public const string UnreachableBroker = "amqp://guest:guest@127.0.0.1:1/";

    /// <summary>
    /// CrewCall.Reporting in-process. It gets only its own database and the broker: no operational connection string and no
    /// API address exist in its configuration.
    /// </summary>
    public static WebApplicationFactory<reporting::Program> Reporting(ReportingInfrastructure infrastructure, string queueName, string? broker = null) =>
        new WebApplicationFactory<reporting::Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall-reporting", infrastructure.ReportingConnectionString)
            .UseSetting("ConnectionStrings:crewcall-rabbitmq", broker ?? infrastructure.BrokerConnectionString)
            .UseSetting("Reporting:QueueName", queueName)
            .UseSetting("Reporting:ReconnectDelay", "00:00:00.200")
            .UseSetting("Reporting:RequeueDelay", "00:00:00.100"));

    public static string NewQueueName() => $"crewcall.reporting.test-{Guid.NewGuid():N}";

    public static RabbitMqConnection Broker(ReportingInfrastructure infrastructure) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:crewcall-rabbitmq"] = infrastructure.BrokerConnectionString })
            .Build(), "crewcall-reporting-tests");

    /// <summary>Publishes envelopes to crewcall.events as the outbox publisher does (routing key, MessageId, persistent).</summary>
    public static async Task PublishAsync(RabbitMqConnection broker, IEnumerable<IntegrationEventEnvelope> envelopes)
    {
        await using var channel = await (await broker.GetAsync(TestContext.Current.CancellationToken))
            .CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await IntegrationExchange.DeclareAsync(channel, TestContext.Current.CancellationToken);
        foreach (var envelope in envelopes)
        {
            var properties = new BasicProperties
            {
                MessageId = envelope.MessageId.ToString(),
                Type = envelope.Type,
                ContentType = IntegrationEventEnvelope.ContentType,
                DeliveryMode = DeliveryModes.Persistent
            };
            await channel.BasicPublishAsync(
                IntegrationExchange.Name, IntegrationEventCatalog.RoutingKeyFor(envelope.Type), mandatory: false, properties,
                envelope.ToUtf8Bytes(), TestContext.Current.CancellationToken);
        }
    }

    public static async Task DeleteQueueAsync(ReportingInfrastructure infrastructure, string queueName)
    {
        await using var broker = Broker(infrastructure);
        await using var channel = await (await broker.GetAsync(CancellationToken.None)).CreateChannelAsync();
        await channel.QueueDeleteAsync(queueName);
    }
}

internal static class Wait
{
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(150);
        }
    }
}
