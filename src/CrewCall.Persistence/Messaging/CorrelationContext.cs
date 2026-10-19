namespace CrewCall.Persistence.Messaging;

/// <summary>
/// The correlation id of the current request or operation, when it has one (ADR-0014). Scoped: set once per request
/// (e.g. from the X-Correlation-Id header) and read when events are written.
/// </summary>
public sealed class CorrelationContext
{
    public Guid? CorrelationId { get; set; }
}
