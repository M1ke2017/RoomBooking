using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence.Messaging;

/// <summary>
/// The outbox publisher's database side (ADR-0014). Claiming is one short statement: it takes a batch of eligible
/// messages with FOR UPDATE SKIP LOCKED and leases them to one publisher, so concurrent publishers never take the same
/// message at the same time, and no transaction stays open while messages travel to the broker. The result of each
/// publish is recorded afterwards, again in a short statement. A publisher that dies after the broker confirmed but
/// before the result was recorded leaves a lease that expires: the message is published again (at-least-once).
/// </summary>
public sealed class OutboxStore(CrewCallDbContext db)
{
    /// <summary>
    /// Leases up to <paramref name="batchSize"/> pending messages (oldest first by CreatedAtUtc, Id) to
    /// <paramref name="publisherId"/> until <paramref name="now"/> + <paramref name="lease"/>. Pending: not processed, not
    /// failed, past its backoff, and not leased to anyone (or the lease expired).
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        string publisherId, int batchSize, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var leasedUntil = now + lease;
        var claimed = await db.OutboxMessages
            .FromSql($"""
                UPDATE ops.outbox_messages
                SET locked_until_utc = {leasedUntil}, locked_by = {publisherId}
                WHERE id IN (
                    SELECT id FROM ops.outbox_messages
                    WHERE processed_at_utc IS NULL
                      AND failed_at_utc IS NULL
                      AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
                      AND (locked_until_utc IS NULL OR locked_until_utc <= {now})
                    ORDER BY created_at_utc, id
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.OrderBy(message => message.CreatedAtUtc).ThenBy(message => message.Id).ToList();
    }

    /// <summary>The broker confirmed the message: it is processed and never claimed again.</summary>
    public Task MarkPublishedAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.OutboxMessages
            .Where(message => message.Id == messageId && message.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(message => message.ProcessedAtUtc, now)
                    .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1)
                    .SetProperty(message => message.LastAttemptAtUtc, now)
                    .SetProperty(message => message.LastError, (string?)null)
                    .SetProperty(message => message.NextAttemptAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LockedBy, (string?)null),
                cancellationToken);

    /// <summary>
    /// The attempt failed: the message stays pending with the error, not retried before <paramref name="nextAttemptAtUtc"/>;
    /// with <paramref name="failed"/> it is dead-lettered (kept, never retried automatically).
    /// </summary>
    public Task MarkAttemptFailedAsync(
        Guid messageId, string error, DateTimeOffset now, DateTimeOffset nextAttemptAtUtc, bool failed, CancellationToken cancellationToken)
    {
        var lastError = error.Length > OutboxMessage.LastErrorMaxLength ? error[..OutboxMessage.LastErrorMaxLength] : error;
        return db.OutboxMessages
            .Where(message => message.Id == messageId && message.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1)
                    .SetProperty(message => message.LastAttemptAtUtc, now)
                    .SetProperty(message => message.LastError, lastError)
                    .SetProperty(message => message.NextAttemptAtUtc, (DateTimeOffset?)nextAttemptAtUtc)
                    .SetProperty(message => message.FailedAtUtc, failed ? now : null)
                    .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LockedBy, (string?)null),
                cancellationToken);
    }

    /// <summary>Gives leased messages back unattempted (e.g. the rest of a batch after the broker failed).</summary>
    public Task ReleaseAsync(string publisherId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken) =>
        messageIds.Count == 0
            ? Task.CompletedTask
            : db.OutboxMessages
                .Where(message => messageIds.Contains(message.Id) && message.LockedBy == publisherId && message.ProcessedAtUtc == null)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(message => message.LockedUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(message => message.LockedBy, (string?)null),
                    cancellationToken);

    /// <summary>Pending (not processed, not failed) and failed (dead-lettered) counts, for diagnostics.</summary>
    public async Task<(int Pending, int Failed)> CountsAsync(CancellationToken cancellationToken) =>
        (await db.OutboxMessages.CountAsync(message => message.ProcessedAtUtc == null && message.FailedAtUtc == null, cancellationToken),
         await db.OutboxMessages.CountAsync(message => message.FailedAtUtc != null, cancellationToken));
}
