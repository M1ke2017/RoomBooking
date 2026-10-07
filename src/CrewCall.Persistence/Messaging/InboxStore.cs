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

/// <summary>
/// Consumer idempotency (ADR-0014): the inbox record and the consumer's effect are written in one transaction. The inbox
/// insert uses ON CONFLICT DO NOTHING on the MessageId, so a redelivery, even one handled concurrently, finds the first
/// delivery's record and skips the effect.
/// </summary>
public sealed class InboxStore(CrewCallDbContext db)
{
    /// <summary>
    /// Records the message and, only on its first delivery, runs <paramref name="effect"/> (which adds its changes to the
    /// context), then commits both together.
    /// </summary>
    public Task<InboxResult> ProcessOnceAsync(
        IntegrationEventEnvelope envelope, DateTimeOffset now, Action<CrewCallDbContext> effect, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ops.inbox_messages (message_id, type, received_at_utc, processed_at_utc)
                VALUES ({envelope.MessageId}, {envelope.Type}, {now}, {now})
                ON CONFLICT (message_id) DO NOTHING
                """,
                token);

            if (inserted == 0)
            {
                await transaction.RollbackAsync(token);
                return InboxResult.Duplicate;
            }

            effect(db);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return InboxResult.Processed;
        }, cancellationToken);

    /// <summary>The integration audit effect: one receipt per message.</summary>
    public static void AddReceipt(CrewCallDbContext db, IntegrationEventEnvelope envelope, DateTimeOffset now) =>
        db.IntegrationEventReceipts.Add(new IntegrationEventReceipt(
            Guid.CreateVersion7(), envelope.MessageId, envelope.Type, envelope.Version, envelope.OccurredAtUtc, envelope.CorrelationId, now));
}
