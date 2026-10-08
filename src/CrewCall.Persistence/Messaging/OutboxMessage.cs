namespace CrewCall.Persistence.Messaging;

/// <summary>
/// One integration event waiting to be published (ops.outbox_messages, ADR-0014). Written by the same SaveChanges, in
/// the same transaction, as the business change it announces; published later by the outbox publisher. Never deleted:
/// a processed or failed message stays as history.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 100;
    public const int RoutingKeyMaxLength = 100;
    public const int AggregateTypeMaxLength = 50;
    public const int LastErrorMaxLength = 2000;
    public const int LockedByMaxLength = 100;

    internal OutboxMessage(
        Guid id,
        DateTimeOffset occurredAtUtc,
        string type,
        int version,
        string routingKey,
        string payload,
        Guid? correlationId,
        string? aggregateType,
        Guid? aggregateId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OccurredAtUtc = occurredAtUtc;
        Type = type;
        Version = version;
        RoutingKey = routingKey;
        Payload = payload;
        CorrelationId = correlationId;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        CreatedAtUtc = createdAtUtc;
    }

    // For EF Core materialization.
    private OutboxMessage()
    {
        Type = string.Empty;
        RoutingKey = string.Empty;
        Payload = string.Empty;
    }

    /// <summary>The integration event id; also the broker MessageId.</summary>
    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Stable public event type, e.g. "visit.work-completed".</summary>
    public string Type { get; private set; }

    public int Version { get; private set; }

    public string RoutingKey { get; private set; }

    /// <summary>The event's payload JSON (jsonb).</summary>
    public string Payload { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public string? AggregateType { get; private set; }

    public Guid? AggregateId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Set only after the broker confirmed the message.</summary>
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>After a failed attempt: not retried before this (backoff).</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>A publisher's short claim (lease): no other publisher takes the message before this.</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public string? LockedBy { get; private set; }

    /// <summary>Dead-lettered after the maximum number of attempts: kept, no longer retried, ProcessedAtUtc stays null.</summary>
    public DateTimeOffset? FailedAtUtc { get; private set; }
}
