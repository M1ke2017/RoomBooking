namespace CrewCall.Persistence.Messaging;

/// <summary>
/// A message a consumer has handled (ops.inbox_messages): the idempotency record. Written in the same transaction as the
/// consumer's effect, so a redelivered message (same MessageId) finds it and the effect does not run twice.
/// </summary>
public sealed class InboxMessage
{
    public const int TypeMaxLength = 100;

    internal InboxMessage(Guid messageId, string type, DateTimeOffset receivedAtUtc, DateTimeOffset processedAtUtc)
    {
        MessageId = messageId;
        Type = type;
        ReceivedAtUtc = receivedAtUtc;
        ProcessedAtUtc = processedAtUtc;
    }

    // For EF Core materialization.
    private InboxMessage()
    {
        Type = string.Empty;
    }

    public Guid MessageId { get; private set; }

    public string Type { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }
}

/// <summary>The integration audit consumer's effect (ops.integration_event_receipts): one technical receipt per message.</summary>
public sealed class IntegrationEventReceipt
{
    internal IntegrationEventReceipt(
        Guid id, Guid messageId, string eventType, int eventVersion, DateTimeOffset occurredAtUtc, Guid? correlationId, DateTimeOffset receivedAtUtc)
    {
        Id = id;
        MessageId = messageId;
        EventType = eventType;
        EventVersion = eventVersion;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
        ReceivedAtUtc = receivedAtUtc;
    }

    // For EF Core materialization.
    private IntegrationEventReceipt()
    {
        EventType = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string EventType { get; private set; }

    public int EventVersion { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }
}
