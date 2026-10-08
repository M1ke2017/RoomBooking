namespace CrewCall.Contracts.Integration;

/// <summary>
/// A fact published to other processes (ADR-0014). Unlike an operational event (the application's own detailed history),
/// an integration event is a stable, versioned contract: small, explicit, and never an EF entity. Its public name and
/// version come from <see cref="IntegrationEventCatalog"/>, never from the .NET type name.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique per event; also the outbox message id and the broker message id.</summary>
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }

    /// <summary>The request or flow that caused the event, when known.</summary>
    Guid? CorrelationId { get; }
}
