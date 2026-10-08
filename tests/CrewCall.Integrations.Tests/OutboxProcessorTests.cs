using CrewCall.Contracts.Integration;
using CrewCall.Persistence;
using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Integrations.Tests;

/// <summary>The outbox publisher's processing on a real PostgreSQL, with a fake broker (ADR-0014).</summary>
[Collection(Sequential.Name)]
public sealed class OutboxProcessorTests(MessagingInfrastructure infrastructure) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2038, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await infrastructure.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Writes <paramref name="count"/> outbox messages, one second apart, as a business transaction would.</summary>
    private async Task<List<Guid>> EnqueueAsync(int count)
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var integrationEvent = new AssignmentCancelledIntegrationEvent(Guid.CreateVersion7(), Start.AddSeconds(index), null, Guid.NewGuid(), Guid.NewGuid());
            outbox.Add(integrationEvent, "assignment", integrationEvent.AssignmentId);
            ids.Add(integrationEvent.EventId);
        }

        await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().SaveChangesAsync(Cancellation);
        return ids;
    }

    private async Task<Dictionary<Guid, OutboxMessage>> OutboxAsync()
    {
        await using var services = infrastructure.Services(TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CrewCallDbContext>().OutboxMessages.AsNoTracking()
            .ToDictionaryAsync(message => message.Id, Cancellation);
    }

    [Fact]
    public async Task A_confirmed_message_is_marked_processed_with_its_attempt_and_is_not_selected_again()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var publisher = new FakePublisher();
        var processor = MessagingInfrastructure.Processor(services, publisher, clock);
        var ids = await EnqueueAsync(3);

        var first = await processor.ProcessBatchAsync(Cancellation);
        var second = await processor.ProcessBatchAsync(Cancellation);

        Assert.Equal(new OutboxBatchResult(3, 3, 0), first);
        Assert.Equal(new OutboxBatchResult(0, 0, 0), second);
        Assert.Equal(ids, publisher.Published.Select(message => message.Id)); // oldest first
        var outbox = await OutboxAsync();
        Assert.All(ids, id =>
        {
            var message = outbox[id];
            Assert.Equal((clock.GetUtcNow(), 1, clock.GetUtcNow()), (message.ProcessedAtUtc!.Value, message.AttemptCount, message.LastAttemptAtUtc!.Value));
            Assert.Null(message.LastError);
            Assert.Null(message.LockedBy);
        });
    }

    [Fact]
    public async Task The_batch_limit_is_respected_and_the_rest_waits_for_the_next_batch()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var publisher = new FakePublisher();
        var processor = MessagingInfrastructure.Processor(services, publisher, clock, new OutboxPublisherOptions { BatchSize = 3 });
        var ids = await EnqueueAsync(5);

        Assert.Equal(3, (await processor.ProcessBatchAsync(Cancellation)).Claimed);
        Assert.Equal(ids[..3], publisher.Published.Select(message => message.Id));
        Assert.Equal(2, (await processor.ProcessBatchAsync(Cancellation)).Claimed);
        Assert.Equal(ids, publisher.Published.Select(message => message.Id));
    }

    [Fact]
    public async Task A_failed_publish_leaves_the_message_pending_with_the_error_and_a_backoff_then_a_retry_succeeds()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var publisher = new FakePublisher { Fail = new InvalidOperationException("broker said no") };
        var processor = MessagingInfrastructure.Processor(services, publisher, clock);
        var ids = await EnqueueAsync(3);

        var failed = await processor.ProcessBatchAsync(Cancellation);

        // The first message failed; the rest of the batch was given back untouched (not counted as attempts).
        Assert.Equal(new OutboxBatchResult(3, 0, 1), failed);
        var outbox = await OutboxAsync();
        var message = outbox[ids[0]];
        Assert.Null(message.ProcessedAtUtc);
        Assert.Null(message.FailedAtUtc);
        Assert.Equal(1, message.AttemptCount);
        Assert.Equal("InvalidOperationException: broker said no", message.LastError);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(2), message.NextAttemptAtUtc);
        Assert.All(ids[1..], id => Assert.Equal((0, (string?)null, (DateTimeOffset?)null), (outbox[id].AttemptCount, outbox[id].LockedBy, outbox[id].LastAttemptAtUtc)));

        // The broker is back: the released messages go at once; the failed one only after its backoff.
        publisher.Fail = null;
        Assert.Equal(new OutboxBatchResult(2, 2, 0), await processor.ProcessBatchAsync(Cancellation));
        Assert.Equal(ids[1..], publisher.Published.Select(m => m.Id));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new OutboxBatchResult(1, 1, 0), await processor.ProcessBatchAsync(Cancellation));
        var retried = (await OutboxAsync())[ids[0]];
        Assert.Equal((2, clock.GetUtcNow()), (retried.AttemptCount, retried.ProcessedAtUtc!.Value));
        Assert.Null(retried.LastError);
    }

    [Fact]
    public async Task A_message_failing_every_time_is_dead_lettered_after_the_maximum_attempts_and_kept()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var publisher = new FakePublisher { Fail = new InvalidOperationException("poison") };
        var options = new OutboxPublisherOptions { MaxAttempts = 3, BaseRetryDelay = TimeSpan.FromSeconds(2) };
        var processor = MessagingInfrastructure.Processor(services, publisher, clock, options);
        var id = Assert.Single(await EnqueueAsync(1));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(1, (await processor.ProcessBatchAsync(Cancellation)).Failed);
            Assert.Equal(0, (await processor.ProcessBatchAsync(Cancellation)).Claimed); // not before its backoff: no tight loop
            clock.Advance(options.RetryDelay(attempt));
        }

        Assert.Equal(new OutboxBatchResult(0, 0, 0), await processor.ProcessBatchAsync(Cancellation)); // never again
        var message = (await OutboxAsync())[id];
        Assert.Equal(3, message.AttemptCount);
        Assert.NotNull(message.FailedAtUtc);
        Assert.Null(message.ProcessedAtUtc);
        Assert.Equal(3, publisher.Calls);
        Assert.Equal(TimeSpan.FromSeconds(4), options.RetryDelay(2)); // 2 s, doubling
        Assert.Equal(TimeSpan.FromMinutes(5), options.RetryDelay(20)); // capped
    }

    [Fact]
    public async Task Two_publishers_never_claim_the_same_message_at_the_same_time()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var ids = await EnqueueAsync(40);

        async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string publisherId)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OutboxStore>()
                .ClaimAsync(publisherId, 25, TimeSpan.FromSeconds(30), clock.GetUtcNow(), Cancellation);
        }

        // Concurrently: SKIP LOCKED makes each skip the rows the other is taking.
        var claims = await Task.WhenAll(Task.Run(() => ClaimAsync("publisher-a")), Task.Run(() => ClaimAsync("publisher-b")));
        var a = claims[0].Select(m => m.Id).ToHashSet();
        var b = claims[1].Select(m => m.Id).ToHashSet();

        Assert.Empty(a.Intersect(b));
        Assert.Equal(ids.ToHashSet(), a.Union(b).ToHashSet());

        // Sequentially: a lease keeps the next publisher away until it expires.
        Assert.Empty(await ClaimAsync("publisher-c"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(25, (await ClaimAsync("publisher-c")).Count);
    }

    [Fact]
    public async Task A_claim_skips_messages_another_publisher_has_locked_instead_of_waiting_for_them()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var ids = await EnqueueAsync(3);

        // Another publisher's claim is in flight: it holds the row lock on the oldest message.
        await using var lockingScope = services.CreateAsyncScope();
        var lockingDb = lockingScope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        await using var lockingTransaction = await lockingDb.Database.BeginTransactionAsync(Cancellation);
        await lockingDb.Database.ExecuteSqlAsync($"SELECT id FROM ops.outbox_messages WHERE id = {ids[0]} FOR UPDATE", Cancellation);

        await using var scope = services.CreateAsyncScope();
        var claim = scope.ServiceProvider.GetRequiredService<OutboxStore>()
            .ClaimAsync("publisher-b", 50, TimeSpan.FromSeconds(30), clock.GetUtcNow(), Cancellation);
        var finished = await Task.WhenAny(claim, Task.Delay(TimeSpan.FromSeconds(5), Cancellation));

        Assert.Same(claim, finished); // did not wait for the other publisher
        Assert.Equal(ids[1..], (await claim).Select(message => message.Id));
        await lockingTransaction.RollbackAsync(Cancellation);
    }

    [Fact]
    public async Task A_crash_after_the_broker_confirmed_republishes_the_same_message_id_and_the_inbox_absorbs_it()
    {
        var clock = new ManualClock(Start.AddMinutes(1));
        await using var services = infrastructure.Services(clock);
        var id = Assert.Single(await EnqueueAsync(1));

        // Publisher A claims and publishes, then "crashes" before recording ProcessedAtUtc.
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OutboxStore>().ClaimAsync("publisher-a", 50, TimeSpan.FromSeconds(30), clock.GetUtcNow(), Cancellation);
        }

        var deliveredByA = (await OutboxAsync())[id];

        // After the lease, publisher B publishes it again: at-least-once, the same MessageId.
        clock.Advance(TimeSpan.FromSeconds(31));
        var publisher = new FakePublisher();
        Assert.Equal(new OutboxBatchResult(1, 1, 0), await MessagingInfrastructure.Processor(services, publisher, clock).ProcessBatchAsync(Cancellation));
        var deliveredByB = Assert.Single(publisher.Published);
        Assert.Equal(deliveredByA.Id, deliveredByB.Id);

        // A consumer seeing both deliveries applies the effect once.
        var results = new List<InboxResult>();
        foreach (var delivery in new[] { deliveredByA, deliveredByB })
        {
            await using var scope = services.CreateAsyncScope();
            var envelope = Messaging.RabbitMqMessagePublisher.ToEnvelope(delivery);
            results.Add(await scope.ServiceProvider.GetRequiredService<InboxStore>().ProcessOnceAsync(
                InboxConsumers.IntegrationAudit, envelope, clock.GetUtcNow(), db => InboxStore.AddReceipt(db, envelope, clock.GetUtcNow()), Cancellation));
        }

        Assert.Equal([InboxResult.Processed, InboxResult.Duplicate], results);
        await using var check = services.CreateAsyncScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<CrewCallDbContext>().IntegrationEventReceipts.CountAsync(r => r.MessageId == id, Cancellation));
    }

    [Fact]
    public async Task Without_the_database_nothing_is_published()
    {
        var clock = new ManualClock(Start);
        await using var services = infrastructure.Services(clock, "Host=127.0.0.1;Port=1;Database=crewcall;Username=x;Password=x;Timeout=2");
        var publisher = new FakePublisher();

        await Assert.ThrowsAnyAsync<Exception>(() => MessagingInfrastructure.Processor(services, publisher, clock).ProcessBatchAsync(Cancellation));

        Assert.Equal(0, publisher.Calls);
    }
}
