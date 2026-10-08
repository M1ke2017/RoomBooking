using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Consumers;
using CrewCall.Integrations.Live;
using CrewCall.Integrations.Messaging;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>
/// The live-operations consumer against a real RabbitMQ and PostgreSQL (ADR-0015): its own inbox identity next to the
/// audit consumer, duplicates, SignalR failures, a broker outage, and the full RabbitMQ → SignalR pipeline.
/// Each test uses its own live queue, so messages other tests leave in a shared queue never interfere.
/// </summary>
[Collection(Sequential.Name)]
public sealed class LiveOperationsConsumerTests(MessagingInfrastructure infrastructure) : IAsyncLifetime
{
    private const string LiveConsumer = "crewcall-live-operations";

    private readonly LiveOperationsOptions _options = new()
    {
        QueueName = $"crewcall.live-operations.test-{Guid.NewGuid():N}",
        ReconnectDelay = TimeSpan.FromMilliseconds(200),
        RequeueDelay = TimeSpan.FromMilliseconds(100)
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        await using var broker = infrastructure.Broker();
        await using var channel = await (await broker.GetAsync(CancellationToken.None)).CreateChannelAsync();
        await channel.QueueDeleteAsync(_options.QueueName);
    }

    private LiveOperationsHandler Handler(ServiceProvider services, ILiveOperationsPublisher publisher, TimeProvider? clock = null) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), publisher, Options.Create(_options), clock ?? TimeProvider.System);

    private async Task<(LiveOperationsConsumer Consumer, ListLogger<LiveOperationsConsumer> Logs)> StartConsumerAsync(
        ServiceProvider services, RabbitMqConnection broker, ILiveOperationsPublisher publisher)
    {
        var logs = new ListLogger<LiveOperationsConsumer>();
        var consumer = new LiveOperationsConsumer(broker, Handler(services, publisher), Options.Create(_options), logs);
        await consumer.StartAsync(Cancellation);
        await WaitForLogAsync(logs, "listening");
        return (consumer, logs);
    }

    private static Task WaitForLogAsync(ListLogger<LiveOperationsConsumer> logs, params string[] parts) =>
        Wait.UntilAsync(
            () => Task.FromResult(logs.Messages.Any(m => parts.All(part => m.Contains(part, StringComparison.Ordinal)))),
            $"a log with {string.Join(", ", parts)}");

    private static async Task<OutboxMessage> OutboxForVisitAsync(ServiceProvider services, Guid visitId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.AggregateId == visitId, Cancellation);
    }

    private static async Task<IReadOnlyList<string>> InboxConsumersAsync(ServiceProvider services, Guid messageId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().InboxMessages.AsNoTracking()
            .Where(message => message.MessageId == messageId)
            .OrderBy(message => message.ConsumerName)
            .Select(message => message.ConsumerName)
            .ToListAsync(Cancellation);
    }

    private async Task<uint> QueueDepthAsync(RabbitMqConnection broker)
    {
        await using var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
        return (await channel.QueueDeclarePassiveAsync(_options.QueueName, Cancellation)).MessageCount;
    }

    [Fact]
    public async Task The_audit_and_live_consumers_each_handle_the_same_message_once_and_independently()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        var (visitId, siteId) = await BusinessFlow.CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);
        var envelope = RabbitMqMessagePublisher.ToEnvelope(outbox);
        var signalR = new RecordingLivePublisher();
        var audit = new IntegrationAuditHandler(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var live = Handler(services, signalR);

        Assert.Equal(InboxResult.Processed, await audit.HandleAsync(envelope, Cancellation));
        Assert.Equal(LiveDeliveryResult.Published, await live.HandleAsync(envelope, Cancellation));   // not blocked by the audit record
        Assert.Equal(LiveDeliveryResult.Duplicate, await live.HandleAsync(envelope, Cancellation));
        Assert.Equal(InboxResult.Duplicate, await audit.HandleAsync(envelope, Cancellation));       // not reset by the live record

        Assert.Equal([InboxConsumers.IntegrationAudit, LiveConsumer], await InboxConsumersAsync(services, outbox.Id));
        var broadcast = Assert.Single(signalR.Published);
        Assert.Equal((outbox.Id, LiveOperationTypes.VisitWorkCompleted, visitId), (broadcast.MessageId, broadcast.Type, broadcast.EntityId));
        Assert.Contains(new LiveEntityReference(LiveEntityTypes.Site, siteId), broadcast.Related);
    }

    [Fact]
    public async Task A_message_delivered_twice_is_broadcast_once_and_recorded_once_while_unsupported_and_poison_messages_are_settled()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var broker = infrastructure.Broker();
        await using var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions()));
        var signalR = new RecordingLivePublisher();
        var (consumer, logs) = await StartConsumerAsync(services, broker, signalR);

        var (visitId, _) = await BusinessFlow.CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);
        await publisher.PublishAsync(outbox, Cancellation);
        await publisher.PublishAsync(outbox, Cancellation);
        await WaitForLogAsync(logs, "Duplicate live", outbox.Id.ToString());

        Assert.Single(signalR.Published, m => m.MessageId == outbox.Id);
        Assert.Equal([LiveConsumer], (await InboxConsumersAsync(services, outbox.Id)).Where(c => c == LiveConsumer));

        // A version without a live mapping is ignored on purpose; a payload whose eventId is not its MessageId is poison.
        var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
        var envelope = RabbitMqMessagePublisher.ToEnvelope(outbox);
        var future = envelope with { MessageId = Guid.NewGuid(), Version = 2 };
        var poison = envelope with { MessageId = Guid.NewGuid() };
        foreach (var message in new[] { future, poison })
        {
            await channel.BasicPublishAsync(
                RabbitMqTopology.Exchange, outbox.RoutingKey, mandatory: false, new BasicProperties { MessageId = message.MessageId.ToString() },
                message.ToUtf8Bytes(), Cancellation);
        }

        await WaitForLogAsync(logs, "Ignored", future.MessageId.ToString());
        await WaitForLogAsync(logs, "Rejected", poison.MessageId.ToString());
        Assert.DoesNotContain(signalR.Published, m => m.MessageId == future.MessageId || m.MessageId == poison.MessageId);
        Assert.Empty(await InboxConsumersAsync(services, poison.MessageId));
        Assert.Equal(0u, await QueueDepthAsync(broker));
        await channel.DisposeAsync();
        await consumer.StopAsync(Cancellation);
    }

    [Fact]
    public async Task When_the_SignalR_broadcast_fails_nothing_is_recorded_and_the_message_is_delivered_again_until_it_succeeds()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var broker = infrastructure.Broker();
        await using var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions()));
        var signalR = new RecordingLivePublisher { Fail = new InvalidOperationException("SignalR is down") };
        var (consumer, logs) = await StartConsumerAsync(services, broker, signalR);

        var (visitId, _) = await BusinessFlow.CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);
        await publisher.PublishAsync(outbox, Cancellation);

        await Wait.UntilAsync(() => Task.FromResult(Volatile.Read(ref signalR.Calls) >= 3), "three failed broadcasts");
        Assert.Empty(await InboxConsumersAsync(services, outbox.Id));
        Assert.Contains(logs.Messages, m => m.Contains("will be redelivered", StringComparison.Ordinal));

        signalR.Fail = null;
        await Wait.UntilAsync(() => Task.FromResult(signalR.Published.Any(m => m.MessageId == outbox.Id)), "the successful broadcast");
        await Wait.UntilAsync(async () => (await InboxConsumersAsync(services, outbox.Id)).Count == 1, "the inbox record");
        Assert.Equal(0u, await QueueDepthAsync(broker));
        await consumer.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Broadcasting_to_nobody_is_a_success_and_the_message_is_recorded()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var hub = await LiveHubHost.StartAsync(); // the real SignalR publisher, no client connected
        var (visitId, _) = await BusinessFlow.CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);

        var result = await Handler(services, hub.Publisher).HandleAsync(RabbitMqMessagePublisher.ToEnvelope(outbox), Cancellation);

        Assert.Equal(LiveDeliveryResult.Published, result);
        Assert.Equal([LiveConsumer], await InboxConsumersAsync(services, outbox.Id));
    }

    [Fact]
    public async Task The_routing_lookup_finds_the_site_and_the_technicians_once_per_message()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        var (visitId, siteId) = await BusinessFlow.CreateVisitAsync(services);
        var (current, previous) = (Guid.NewGuid(), Guid.NewGuid());
        var replacedAssignment = await BusinessFlow.InsertAssignmentAsync(services, visitId, previous, "Replaced");
        var activeAssignment = await BusinessFlow.InsertAssignmentAsync(services, visitId, current, "Active");
        await using var scope = services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<ILiveRoutingLookup>();
        var at = LiveTestKit.OccurredAt;

        var completed = await lookup.ResolveAsync(new VisitWorkCompletedIntegrationEvent(Guid.NewGuid(), at, null, visitId, Guid.NewGuid(), null, 1m, 0m, 1m), Cancellation);
        var replaced = await lookup.ResolveAsync(new AssignmentReplacedIntegrationEvent(Guid.NewGuid(), at, null, replacedAssignment, activeAssignment, visitId), Cancellation);
        var cancelled = await lookup.ResolveAsync(new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), at, null, replacedAssignment, visitId), Cancellation);
        var created = await lookup.ResolveAsync(new AssignmentCreatedIntegrationEvent(Guid.NewGuid(), at, null, activeAssignment, visitId, current, null, []), Cancellation);
        var unknownVisit = await lookup.ResolveAsync(new AssignmentCancelledIntegrationEvent(Guid.NewGuid(), at, null, Guid.NewGuid(), Guid.NewGuid()), Cancellation);

        Assert.Equal(siteId, completed.SiteId);
        Assert.Equal([current], completed.TechnicianIds);                                                  // only the active assignment
        Assert.Equal(siteId, replaced.SiteId);
        Assert.Equal(new[] { current, previous }.Order(), replaced.TechnicianIds.Order());
        Assert.Equal([previous], cancelled.TechnicianIds);
        Assert.Equal((siteId, 0), (created.SiteId!.Value, created.TechnicianIds.Count));                  // the event names the technician
        Assert.Equal((null, 0), (unknownVisit.SiteId, unknownVisit.TechnicianIds.Count));                 // fewer groups, no failure
    }

    [Fact]
    public async Task While_RabbitMQ_is_down_the_business_still_commits_and_the_live_consumer_receives_the_event_once_it_is_back()
    {
        var clock = new ManualClock(new DateTimeOffset(2038, 6, 3, 12, 0, 0, TimeSpan.Zero));
        await using var services = infrastructure.Services(clock);
        await using var proxy = new TcpProxy(new Uri(infrastructure.BrokerConnectionString));
        proxy.Up();
        await using var broker = infrastructure.Broker(proxy.ConnectionString);
        var signalR = new RecordingLivePublisher();
        var (consumer, logs) = await StartConsumerAsync(services, broker, signalR);

        // The broker becomes unreachable. The business operation does not need it and commits to the outbox.
        proxy.Down();
        await WaitForLogAsync(logs, "could not connect");
        var (visitId, _) = await BusinessFlow.CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);
        await using (var downPublisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions())))
        {
            Assert.Equal(new OutboxBatchResult(1, 0, 1), await MessagingInfrastructure.Processor(services, downPublisher, clock).ProcessBatchAsync(Cancellation));
        }

        Assert.Empty(signalR.Published);

        // The broker is back: the outbox is published, the consumer reconnects by itself and broadcasts the event.
        proxy.Up();
        clock.Advance(TimeSpan.FromSeconds(3));
        await using (var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions())))
        {
            Assert.Equal(new OutboxBatchResult(1, 1, 0), await MessagingInfrastructure.Processor(services, publisher, clock).ProcessBatchAsync(Cancellation));
        }

        await Wait.UntilAsync(() => Task.FromResult(signalR.Published.Any(m => m.MessageId == outbox.Id)), "the live broadcast");
        await Wait.UntilAsync(async () => (await InboxConsumersAsync(services, outbox.Id)).Count == 1, "the inbox record");
        await consumer.StopAsync(Cancellation);
    }

    [Fact]
    public async Task End_to_end_a_completed_visit_reaches_a_SignalR_client_with_the_outbox_id_as_MessageId()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var integrations = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.DatabaseConnectionString)
            .UseSetting("ConnectionStrings:crewcall-rabbitmq", infrastructure.BrokerConnectionString)
            .UseSetting("Outbox:PollInterval", "00:00:00.200")
            .UseSetting("LiveOperations:QueueName", _options.QueueName));
        var hubUrl = new Uri(integrations.Server.BaseAddress, LiveOperationsHubContract.Path);
        var (visitId, siteId) = await BusinessFlow.CreateVisitAsync(services);

        await using var everything = await LiveHubClient.ConnectAsync(hubUrl, () => integrations.Server.CreateHandler());
        await using var site = await LiveHubClient.ConnectAsync(hubUrl, () => integrations.Server.CreateHandler());
        await everything.InvokeAsync(LiveOperationsHubContract.SubscribeAll);
        await site.InvokeAsync(LiveOperationsHubContract.SubscribeSite, siteId);

        // Business operation → operational event + outbox (one transaction) → publisher → RabbitMQ → live consumer → SignalR.
        await BusinessFlow.CompleteWorkAsync(services, visitId);
        var outbox = await OutboxForVisitAsync(services, visitId);

        var received = await everything.NextAsync();
        Assert.Equal(
            (outbox.Id, "visit.work.completed", "visit", visitId, "completed", outbox.OccurredAtUtc),
            (received.MessageId, received.Type, received.EntityType, received.EntityId, received.Action, received.OccurredAtUtc));
        Assert.Equal(outbox.Id, (await site.NextAsync()).MessageId);

        // The audit consumer handled the same message too: one inbox record per consumer.
        await Wait.UntilAsync(async () => (await InboxConsumersAsync(services, outbox.Id)).Count == 2, "both inbox records");
        Assert.Equal([InboxConsumers.IntegrationAudit, LiveConsumer], await InboxConsumersAsync(services, outbox.Id));
    }
}
