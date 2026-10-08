using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Messaging;

namespace CrewCall.Reporting.Projections;

/// <summary>
/// The integration events the reporting projections use, read from their envelopes (ADR-0016). An explicit table of
/// (type, version) pairs: anything else is not projected, on purpose.
/// </summary>
public static class ReportingEventReader
{
    /// <summary>The routing keys the crewcall.reporting queue is bound to: exactly the projected events.</summary>
    public static IReadOnlyList<string> SubscribedRoutingKeys { get; } =
    [
        IntegrationEventCatalog.AssignmentCreated.RoutingKey,
        IntegrationEventCatalog.AssignmentReplaced.RoutingKey,
        IntegrationEventCatalog.AssignmentCancelled.RoutingKey,
        IntegrationEventCatalog.IncidentDispatched.RoutingKey,
        IntegrationEventCatalog.VisitWorkCompleted.RoutingKey,
        IntegrationEventCatalog.VisitCreated.RoutingKey,
        IntegrationEventCatalog.VisitStatusChanged.RoutingKey
    ];

    /// <summary>
    /// The event in the envelope, or null when its type and version are not projected. Throws
    /// <see cref="PoisonMessageException"/> when the payload does not match its contract or its eventId is not the
    /// MessageId: such a message can never be projected.
    /// </summary>
    public static IIntegrationEvent? Read(IntegrationEventEnvelope envelope)
    {
        var contract = (envelope.Type, envelope.Version) switch
        {
            ("assignment.created", 1) => typeof(AssignmentCreatedIntegrationEvent),
            ("assignment.replaced", 1) => typeof(AssignmentReplacedIntegrationEvent),
            ("assignment.cancelled", 1) => typeof(AssignmentCancelledIntegrationEvent),
            ("incident.dispatched", 1) => typeof(IncidentDispatchedIntegrationEvent),
            ("visit.work-completed", 1) => typeof(VisitWorkCompletedIntegrationEvent),
            ("visit.created", 1) => typeof(VisitCreatedIntegrationEvent),
            ("visit.status-changed", 1) => typeof(VisitStatusChangedIntegrationEvent),
            _ => null
        };

        if (contract is null)
        {
            return null;
        }

        IIntegrationEvent? integrationEvent;
        try
        {
            integrationEvent = (IIntegrationEvent?)envelope.Payload.Deserialize(contract, IntegrationEventCatalog.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PoisonMessageException($"The {envelope.Type} v{envelope.Version} payload does not match its contract.", exception);
        }

        return integrationEvent is not null && integrationEvent.EventId == envelope.MessageId
            ? integrationEvent
            : throw new PoisonMessageException($"The {envelope.Type} payload's eventId is not the message id {envelope.MessageId}.");
    }
}
