namespace CrewCall.Persistence.Messaging;

/// <summary>
/// A message a consumer has handled (ops.inbox_messages): the idempotency record, keyed by consumer and MessageId
/// (ADR-0015). Each consumer has its own record, so the same message is handled once by every consumer, and a redelivery
/// to the same consumer finds its record and is skipped.
/// </summary>
public sealed class InboxMessage
{
    public const int ConsumerNameMaxLength = 100;
    public const int TypeMaxLength = 100;

    internal InboxMessage(string consumerName, Guid messageId, string type, DateTimeOffset receivedAtUtc, DateTimeOffset processedAtUtc)
    {
        ConsumerName = consumerName;
        MessageId = messageId;
        Type = type;
        ReceivedAtUtc = receivedAtUtc;
        ProcessedAtUtc = processedAtUtc;
    }

    // For EF Core materialization.
    private InboxMessage()
    {
        ConsumerName = string.Empty;
        Type = string.Empty;
    }

    /// <summary>The consumer that handled the message, e.g. "crewcall-integration-audit".</summary>
    public string ConsumerName { get; private set; }

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
