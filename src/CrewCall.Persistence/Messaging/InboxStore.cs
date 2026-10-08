using CrewCall.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence.Messaging;

public enum InboxResult
{
    /// <summary>First delivery: the effect ran and was committed together with the inbox record.</summary>
    Processed,

    /// <summary>The message was handled before (same MessageId): nothing was done again.</summary>
    Duplicate
}

/// <summary>The consumer names used as inbox identities (ADR-0015).</summary>
public static class InboxConsumers
{
    /// <summary>The technical integration-audit consumer (Sprint 12).</summary>
    public const string IntegrationAudit = "crewcall-integration-audit";
}

/// <summary>
/// Consumer idempotency (ADR-0014, ADR-0015). Every record is keyed by (consumer name, MessageId): a message is handled
/// once per consumer, and two consumers handle the same message independently.
/// - <see cref="ProcessOnceAsync"/>: for an effect in this database, the inbox record and the effect are written in one
///   transaction; the insert uses ON CONFLICT DO NOTHING, so a redelivery, even one handled concurrently, skips the effect.
/// - <see cref="IsProcessedAsync"/> and <see cref="MarkProcessedAsync"/>: for an effect outside the database (a SignalR
///   broadcast), which cannot share the transaction; the record is written after the effect succeeded.
/// </summary>
public sealed class InboxStore(CrewCallDbContext db)
{
    /// <summary>
    /// Records the message for <paramref name="consumerName"/> and, only on its first delivery to that consumer, runs
    /// <paramref name="effect"/> (which adds its changes to the context), then commits both together.
    /// </summary>
    public Task<InboxResult> ProcessOnceAsync(
        string consumerName, IntegrationEventEnvelope envelope, DateTimeOffset now, Action<CrewCallDbContext> effect,
        CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            if (!await InsertAsync(consumerName, envelope, now, token))
            {
                await transaction.RollbackAsync(token);
                return InboxResult.Duplicate;
            }

            effect(db);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return InboxResult.Processed;
        }, cancellationToken);

    /// <summary>Whether <paramref name="consumerName"/> has already handled the message.</summary>
    public Task<bool> IsProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken) =>
        db.InboxMessages.AsNoTracking().AnyAsync(
            message => message.ConsumerName == consumerName && message.MessageId == messageId, cancellationToken);

    /// <summary>
    /// Records that <paramref name="consumerName"/> handled the message. Returns false when it was already recorded (a
    /// concurrent delivery got there first), which is not an error.
    /// </summary>
    public Task<bool> MarkProcessedAsync(
        string consumerName, IntegrationEventEnvelope envelope, DateTimeOffset now, CancellationToken cancellationToken) =>
        InsertAsync(consumerName, envelope, now, cancellationToken);

    private async Task<bool> InsertAsync(string consumerName, IntegrationEventEnvelope envelope, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ops.inbox_messages (consumer_name, message_id, type, received_at_utc, processed_at_utc)
            VALUES ({consumerName}, {envelope.MessageId}, {envelope.Type}, {now}, {now})
            ON CONFLICT (consumer_name, message_id) DO NOTHING
            """,
            cancellationToken) == 1;

    /// <summary>The integration audit effect: one receipt per message.</summary>
    public static void AddReceipt(CrewCallDbContext db, IntegrationEventEnvelope envelope, DateTimeOffset now) =>
        db.IntegrationEventReceipts.Add(new IntegrationEventReceipt(
            Guid.CreateVersion7(), envelope.MessageId, envelope.Type, envelope.Version, envelope.OccurredAtUtc, envelope.CorrelationId, now));
}
