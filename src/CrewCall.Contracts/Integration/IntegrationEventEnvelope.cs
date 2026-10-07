using System.Text.Json;

namespace CrewCall.Contracts.Integration;

/// <summary>
/// The message body on the broker (content type application/json). MessageId is the outbox message id, so it is the same
/// from the outbox to every consumer's inbox, and stays the same when a message is delivered more than once.
/// </summary>
/// <param name="Type">The stable event type, e.g. "visit.work-completed".</param>
/// <param name="Payload">The event's own fields, as a JSON object.</param>
public sealed record IntegrationEventEnvelope(
    Guid MessageId,
    string Type,
    int Version,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    JsonElement Payload)
{
    public const string ContentType = "application/json";

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, IntegrationEventCatalog.JsonOptions);

    /// <summary>The envelope, or null with the reason when the body is not a valid envelope.</summary>
    public static IntegrationEventEnvelope? TryParse(ReadOnlySpan<byte> body, out string? error)
    {
        IntegrationEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(body, IntegrationEventCatalog.JsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"Not a JSON envelope: {exception.Message}";
            return null;
        }

        error = envelope switch
        {
            null => "Empty body.",
            { MessageId: var id } when id == Guid.Empty => "MessageId is required.",
            { Type: var type } when string.IsNullOrWhiteSpace(type) => "Type is required.",
            { Version: <= 0 } => "Version must be positive.",
            { Payload.ValueKind: not JsonValueKind.Object } => "Payload must be a JSON object.",
            _ => null
        };

        return error is null ? envelope : null;
    }
}
