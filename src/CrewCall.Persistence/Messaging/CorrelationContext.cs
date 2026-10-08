namespace CrewCall.Persistence.Messaging;

/// <summary>The correlation id of the current request or operation, when it has one (ADR-0014).</summary>
public interface ICorrelationContext
{
    Guid? CorrelationId { get; }
}

/// <summary>Scoped: set once per request (e.g. from the X-Correlation-Id header) and read when events are written.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public Guid? CorrelationId { get; set; }
}
