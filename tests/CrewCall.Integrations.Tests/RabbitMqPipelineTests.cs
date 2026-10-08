using System.Text;
using CrewCall.Messaging;
using CrewCall.Contracts.Integration;
using CrewCall.Integrations.Consumers;
using CrewCall.Integrations.Messaging;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>The publisher and the consumer against a real RabbitMQ and PostgreSQL (ADR-0014).</summary>
[Collection(Sequential.Name)]
public sealed class RabbitMqPipelineTests(MessagingInfrastructure infrastructure) : IAsyncLifetime
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A visit whose field work is completed through the WorkOrders module: the business transaction under test.</summary>
    private static async Task<Guid> CompleteVisitWorkAsync(ServiceProvider services) =>
        (await BusinessFlow.CompleteVisitWorkAsync(services)).VisitId;

    /// <summary>
    /// The visit's outbox message of one type. Since Sprint 14 a completed visit has four: visit.created, two
    /// visit.status-changed (InProgress, Completed) and visit.work-completed.
    /// </summary>
    private static async Task<OutboxMessage> OutboxForVisitAsync(ServiceProvider services, Guid visitId, string type = "visit.work-completed")
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.AggregateId == visitId && message.Type == type, Cancellation);
    }

    private static async Task<(int Inbox, int Receipts)> ConsumedAsync(ServiceProvider services, Guid messageId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        return (await db.InboxMessages.CountAsync(m => m.MessageId == messageId && m.ConsumerName == InboxConsumers.IntegrationAudit, Cancellation),
                await db.IntegrationEventReceipts.CountAsync(r => r.MessageId == messageId, Cancellation));
    }

    /// <summary>A private, auto-deleted queue bound to the exchange, to observe what the publisher sent.</summary>
    private static async Task<(IChannel Channel, string Queue)> ObserveAsync(RabbitMqConnection broker, string bindingKey)
    {
        var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
        await RabbitMqTopology.DeclareExchangeAsync(channel, Cancellation);
        var queue = (await channel.QueueDeclareAsync(cancellationToken: Cancellation)).QueueName;
        await channel.QueueBindAsync(queue, RabbitMqTopology.Exchange, bindingKey, cancellationToken: Cancellation);
        return (channel, queue);
    }

    private static async Task<BasicGetResult> NextAsync(IChannel channel, string queue)
    {
        BasicGetResult? result = null;
        await Wait.UntilAsync(async () => (result = await channel.BasicGetAsync(queue, autoAck: true, Cancellation)) is not null, "a message");
        return result!;
    }

    [Fact]
    public async Task A_confirmed_publish_reaches_the_topic_exchange_with_its_routing_key_message_id_and_envelope()
    {
        var clock = new ManualClock(new DateTimeOffset(2038, 6, 1, 12, 0, 0, TimeSpan.Zero));
        await using var services = infrastructure.Services(clock);
        await using var broker = infrastructure.Broker();
        await using var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions()));
        var (observer, queue) = await ObserveAsync(broker, "visit.work.completed");
        var visitId = await CompleteVisitWorkAsync(services);

        var result = await MessagingInfrastructure.Processor(services, publisher, clock).ProcessBatchAsync(Cancellation);

        Assert.Equal(new Integrations.OutboxBatchResult(4, 4, 0), result); // the visit's four messages
        var outbox = await OutboxForVisitAsync(services, visitId);
        Assert.NotNull(outbox.ProcessedAtUtc); // only after the broker's confirmation
        await observer.ExchangeDeclarePassiveAsync("crewcall.events", Cancellation); // the topic exchange exists

        var delivered = await NextAsync(observer, queue);
        Assert.Equal(("visit.work.completed", "crewcall.events"), (delivered.RoutingKey, delivered.Exchange));
        Assert.Equal((outbox.Id.ToString(), "visit.work-completed", "application/json"),
            (delivered.BasicProperties.MessageId, delivered.BasicProperties.Type, delivered.BasicProperties.ContentType));
        Assert.Equal(DeliveryModes.Persistent, delivered.BasicProperties.DeliveryMode);
        var envelope = IntegrationEventEnvelope.TryParse(delivered.Body.Span, out var error);
        Assert.Null(error);
        Assert.Equal((outbox.Id, "visit.work-completed", 1), (envelope!.MessageId, envelope.Type, envelope.Version));
        Assert.Equal(visitId, envelope.Payload.GetProperty("visitId").GetGuid());
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task The_consumer_records_a_message_once_even_when_it_is_delivered_twice_and_rejects_invalid_messages()
    {
        var clock = TimeProvider.System;
        await using var services = infrastructure.Services(clock);
        await using var broker = infrastructure.Broker();
        await using var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions()));
        var logger = new ListLogger<IntegrationAuditConsumer>();
        var consumer = new IntegrationAuditConsumer(broker, new IntegrationAuditHandler(services.GetRequiredService<IServiceScopeFactory>(), clock), logger);
        await consumer.StartAsync(Cancellation);
        await Wait.UntilAsync(() => Task.FromResult(logger.Messages.Any(m => m.Contains("listening", StringComparison.Ordinal))), "the consumer");

        var visitId = await CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);

        // At-least-once: the same outbox message is published twice (e.g. a publisher crashed after the confirm).
        await publisher.PublishAsync(outbox, Cancellation);
        await publisher.PublishAsync(outbox, Cancellation);
        await Wait.UntilAsync(() => Task.FromResult(logger.Messages.Any(m => m.Contains("Duplicate", StringComparison.Ordinal) && m.Contains(outbox.Id.ToString(), StringComparison.Ordinal))), "the duplicate");

        Assert.Equal((1, 1), await ConsumedAsync(services, outbox.Id));

        // A body that is not an envelope is rejected without requeue and leaves no trace.
        var channel = await (await broker.GetAsync(Cancellation)).CreateChannelAsync(cancellationToken: Cancellation);
        var bogusId = Guid.NewGuid();
        await channel.BasicPublishAsync(
            RabbitMqTopology.Exchange, "bogus.event", mandatory: false, new BasicProperties { MessageId = bogusId.ToString() },
            Encoding.UTF8.GetBytes("{\"not\":\"an envelope\"}"), Cancellation);
        await Wait.UntilAsync(() => Task.FromResult(logger.Messages.Any(m => m.Contains("Rejected", StringComparison.Ordinal) && m.Contains(bogusId.ToString(), StringComparison.Ordinal))), "the rejection");
        Assert.Equal((0, 0), await ConsumedAsync(services, bogusId));

        // Every delivery was settled: nothing is left in the audit queue.
        var queue = await channel.QueueDeclarePassiveAsync(RabbitMqTopology.AuditQueue, Cancellation);
        Assert.Equal(0u, queue.MessageCount);
        await channel.DisposeAsync();
        await consumer.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_message_produce_one_inbox_entry_and_one_receipt()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        var payload = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
        var envelope = new IntegrationEventEnvelope(Guid.NewGuid(), "assignment.cancelled", 1, DateTimeOffset.UtcNow, null, payload);
        var handler = new IntegrationAuditHandler(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => handler.HandleAsync(envelope, Cancellation))));

        Assert.Single(results, result => result == InboxResult.Processed);
        Assert.Equal(3, results.Count(result => result == InboxResult.Duplicate));
        Assert.Equal((1, 1), await ConsumedAsync(services, envelope.MessageId));
    }

    [Fact]
    public async Task While_the_broker_is_unreachable_the_business_commits_the_message_waits_and_it_is_published_once_the_broker_is_back()
    {
        var clock = new ManualClock(new DateTimeOffset(2038, 6, 2, 12, 0, 0, TimeSpan.Zero));
        await using var services = infrastructure.Services(clock);

        // The business transaction does not involve the broker at all: it commits, and the message waits in the outbox.
        var visitId = await CompleteVisitWorkAsync(services);
        // The visit's oldest message is the one the failing pass attempts; the rest of the batch is released.
        var pending = await OutboxForVisitAsync(services, visitId, "visit.created");
        Assert.Null(pending.ProcessedAtUtc);

        await using (var unreachable = infrastructure.Broker("amqp://guest:guest@127.0.0.1:1/"))
        await using (var downPublisher = new RabbitMqMessagePublisher(unreachable, Options.Create(new OutboxPublisherOptions())))
        {
            var attempt = await MessagingInfrastructure.Processor(services, downPublisher, clock).ProcessBatchAsync(Cancellation);
            Assert.Equal(new Integrations.OutboxBatchResult(4, 0, 1), attempt);
        }

        var waiting = await OutboxForVisitAsync(services, visitId, "visit.created");
        Assert.Null(waiting.ProcessedAtUtc);
        Assert.Equal(1, waiting.AttemptCount);
        Assert.False(string.IsNullOrEmpty(waiting.LastError));

        // The broker is reachable again: after the backoff the same message is published.
        await using var broker = infrastructure.Broker();
        await using var publisher = new RabbitMqMessagePublisher(broker, Options.Create(new OutboxPublisherOptions()));
        var (observer, queue) = await ObserveAsync(broker, "visit.created");
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(new Integrations.OutboxBatchResult(4, 4, 0), await MessagingInfrastructure.Processor(services, publisher, clock).ProcessBatchAsync(Cancellation));
        Assert.Equal(pending.Id.ToString(), (await NextAsync(observer, queue)).BasicProperties.MessageId);
        Assert.Equal(2, (await OutboxForVisitAsync(services, visitId, "visit.created")).AttemptCount);
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task End_to_end_a_completed_visit_travels_from_the_outbox_through_RabbitMQ_to_the_consumer_inbox_with_one_message_id()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var integrations = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:crewcall", infrastructure.DatabaseConnectionString)
            .UseSetting("ConnectionStrings:crewcall-rabbitmq", infrastructure.BrokerConnectionString)
            .UseSetting("Outbox:PollInterval", "00:00:00.200"));
        _ = integrations.Services; // starts the publisher and the consumer

        var visitId = await CompleteVisitWorkAsync(services);
        var outbox = await OutboxForVisitAsync(services, visitId);

        await Wait.UntilAsync(async () => (await ConsumedAsync(services, outbox.Id)).Receipts == 1, "the receipt");

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        var receipt = await db.IntegrationEventReceipts.AsNoTracking().SingleAsync(r => r.MessageId == outbox.Id, Cancellation);
        // One inbox record per consumer (ADR-0015); this is the audit consumer's.
        var inbox = await db.InboxMessages.AsNoTracking()
            .SingleAsync(m => m.MessageId == outbox.Id && m.ConsumerName == InboxConsumers.IntegrationAudit, Cancellation);
        Assert.Equal(("visit.work-completed", 1), (receipt.EventType, receipt.EventVersion));
        Assert.Equal(("visit.work-completed", outbox.Id), (inbox.Type, inbox.MessageId));
        Assert.NotNull(inbox.ProcessedAtUtc);
        Assert.NotNull((await OutboxForVisitAsync(services, visitId)).ProcessedAtUtc);
        var operational = await db.OperationalEvents.AsNoTracking()
            .SingleAsync(e => e.AggregateId == visitId && e.EventType == VisitExecutionEvents.VisitWorkCompleted, Cancellation);
        Assert.Equal(operational.OccurredAtUtc, outbox.OccurredAtUtc);

        using var client = integrations.CreateClient();
        using var health = await client.GetAsync("/health", Cancellation);
        Assert.True(health.IsSuccessStatusCode, await health.Content.ReadAsStringAsync(Cancellation));
    }
}
