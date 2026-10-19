using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Messaging;

namespace CrewCall.Integrations.Live;

/// <summary>
/// The context the integration event itself does not carry, looked up read-only for routing: the site of the visit and
/// technicians that are only known by assignment id (ADR-0015).
/// </summary>
public sealed record LiveRoutingContext(Guid? SiteId, IReadOnlyList<Guid> TechnicianIds)
{
    public static LiveRoutingContext None { get; } = new(null, []);
}

/// <summary>
/// The explicit mapping from integration events to live messages (ADR-0015). The live contract is separate: an integration
/// event is never forwarded 1:1. Only the listed (type, version) pairs are supported; anything else is ignored on purpose.
/// </summary>
public static class LiveOperationMapper
{
    /// <summary>The routing keys the live-operations queue is bound to: exactly the supported events.</summary>
    public static IReadOnlyList<string> SupportedRoutingKeys { get; } =
    [
        IntegrationEventCatalog.AssignmentCreated.RoutingKey,
        IntegrationEventCatalog.AssignmentReplaced.RoutingKey,
        IntegrationEventCatalog.AssignmentCancelled.RoutingKey,
        IntegrationEventCatalog.IncidentDispatched.RoutingKey,
        IntegrationEventCatalog.VisitWorkCompleted.RoutingKey,
        IntegrationEventCatalog.VisitRescheduled.RoutingKey
    ];

    /// <summary>
    /// The integration event in the envelope, or null when its type and version are not supported (e.g. a future v2).
    /// Throws <see cref="PoisonMessageException"/> when the payload does not match its contract or its EventId is not the
    /// envelope's MessageId: such a message can never be mapped.
    /// </summary>
    public static IIntegrationEvent? ReadEvent(IntegrationEventEnvelope envelope)
    {
        var supported = (envelope.Type, envelope.Version) switch
        {
            ("assignment.created", 1) => typeof(AssignmentCreatedIntegrationEvent),
            ("assignment.replaced", 1) => typeof(AssignmentReplacedIntegrationEvent),
            ("assignment.cancelled", 1) => typeof(AssignmentCancelledIntegrationEvent),
            ("incident.dispatched", 1) => typeof(IncidentDispatchedIntegrationEvent),
            ("visit.work-completed", 1) => typeof(VisitWorkCompletedIntegrationEvent),
            ("visit.rescheduled", 1) => typeof(VisitRescheduledIntegrationEvent),
            _ => null
        };

        if (supported is null)
        {
            return null;
        }

        IIntegrationEvent? integrationEvent;
        try
        {
            integrationEvent = (IIntegrationEvent?)envelope.Payload.Deserialize(supported, IntegrationEventCatalog.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PoisonMessageException($"The {envelope.Type} v{envelope.Version} payload does not match its contract.", exception);
        }

        return integrationEvent is not null && integrationEvent.EventId == envelope.MessageId
            ? integrationEvent
            : throw new PoisonMessageException($"The {envelope.Type} payload's eventId is not the message id {envelope.MessageId}.");
    }

    /// <summary>
    /// The live message for a supported integration event: its type and the entity that changed. MessageId and
    /// CorrelationId are the envelope's, unchanged, so the browser sees the outbox id and can deduplicate.
    /// </summary>
    public static LiveOperationMessage Map(IntegrationEventEnvelope envelope, IIntegrationEvent integrationEvent)
    {
        var (type, entityId) = integrationEvent switch
        {
            AssignmentCreatedIntegrationEvent created => (LiveOperationTypes.AssignmentCreated, created.AssignmentId),
            AssignmentReplacedIntegrationEvent replaced => (LiveOperationTypes.AssignmentReplaced, replaced.NewAssignmentId),
            AssignmentCancelledIntegrationEvent cancelled => (LiveOperationTypes.AssignmentCancelled, cancelled.AssignmentId),
            IncidentDispatchedIntegrationEvent dispatched => (LiveOperationTypes.IncidentDispatched, dispatched.IncidentId),
            VisitWorkCompletedIntegrationEvent completed => (LiveOperationTypes.VisitWorkCompleted, completed.VisitId),
            VisitRescheduledIntegrationEvent rescheduled => (LiveOperationTypes.VisitRescheduled, rescheduled.VisitId),
            _ => throw new ArgumentException($"{integrationEvent.GetType().Name} has no live mapping.", nameof(integrationEvent))
        };

        return new LiveOperationMessage(envelope.MessageId, type, entityId, envelope.OccurredAtUtc, envelope.CorrelationId);
    }
}
