namespace CrewCall.Persistence.Operations;

/// <summary>
/// One row of the append-only operational history (ops.operational_events). Written in the same transaction as the
/// state change it describes; never updated or deleted (enforced by a database trigger).
/// </summary>
public sealed class OperationalEvent
{
    public const int EventTypeMaxLength = 100;
    public const int AggregateTypeMaxLength = 50;

    internal OperationalEvent(
        Guid id,
        DateTimeOffset occurredAtUtc,
        string eventType,
        string aggregateType,
        Guid aggregateId,
        string payloadJson,
        Guid? correlationId)
    {
        Id = id;
        OccurredAtUtc = occurredAtUtc;
        EventType = eventType;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        PayloadJson = payloadJson;
        CorrelationId = correlationId;
    }

    public Guid Id { get; private set; }

    /// <summary>Database-assigned, strictly increasing append order; breaks ties between events in the same instant.</summary>
    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventType { get; private set; }

    public string AggregateType { get; private set; }

    public Guid AggregateId { get; private set; }

    public string PayloadJson { get; private set; }

    /// <summary>The request or flow that caused the change, when it carried a correlation id (ADR-0014).</summary>
    public Guid? CorrelationId { get; private set; }
}
