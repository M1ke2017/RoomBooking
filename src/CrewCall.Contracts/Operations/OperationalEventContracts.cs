using System.Text.Json;

namespace CrewCall.Contracts.Operations;

public sealed record OperationalEventResponse(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string AggregateType,
    Guid AggregateId,
    JsonElement Payload,
    Guid? CorrelationId);
