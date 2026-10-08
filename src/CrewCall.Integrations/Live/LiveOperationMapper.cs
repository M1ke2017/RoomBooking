using System.Text.Json;
using CrewCall.Contracts.Integration;
using CrewCall.Contracts.Live;
using CrewCall.Integrations.Consumers;

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
        IntegrationEventCatalog.VisitWorkCompleted.RoutingKey
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
    /// The live message for a supported integration event. MessageId and CorrelationId are the envelope's, unchanged, so
    /// the browser sees the outbox id and can deduplicate.
    /// </summary>
    public static LiveOperationMessage Map(IntegrationEventEnvelope envelope, IIntegrationEvent integrationEvent, LiveRoutingContext routing)
    {
        var (type, entityType, entityId, action, related, summary) = integrationEvent switch
        {
            AssignmentCreatedIntegrationEvent created => (
                LiveOperationTypes.AssignmentCreated, LiveEntityTypes.Assignment, created.AssignmentId, LiveOperationActions.Created,
                Related(routing, [Ref(LiveEntityTypes.Visit, created.VisitId), Ref(LiveEntityTypes.Technician, created.TechnicianId)]),
                $"Assignment created for visit {Short(created.VisitId)}."),

            AssignmentReplacedIntegrationEvent replaced => (
                LiveOperationTypes.AssignmentReplaced, LiveEntityTypes.Assignment, replaced.NewAssignmentId, LiveOperationActions.Replaced,
                Related(routing, [Ref(LiveEntityTypes.Assignment, replaced.OldAssignmentId), Ref(LiveEntityTypes.Visit, replaced.VisitId)]),
                $"Assignment of visit {Short(replaced.VisitId)} replaced."),

            AssignmentCancelledIntegrationEvent cancelled => (
                LiveOperationTypes.AssignmentCancelled, LiveEntityTypes.Assignment, cancelled.AssignmentId, LiveOperationActions.Cancelled,
                Related(routing, [Ref(LiveEntityTypes.Visit, cancelled.VisitId)]),
                $"Assignment of visit {Short(cancelled.VisitId)} cancelled."),

            IncidentDispatchedIntegrationEvent dispatched => (
                LiveOperationTypes.IncidentDispatched, LiveEntityTypes.Incident, dispatched.IncidentId, LiveOperationActions.Dispatched,
                Related(routing,
                [
                    Ref(LiveEntityTypes.WorkOrder, dispatched.WorkOrderId), Ref(LiveEntityTypes.Visit, dispatched.VisitId),
                    Ref(LiveEntityTypes.Assignment, dispatched.AssignmentId), Ref(LiveEntityTypes.Technician, dispatched.TechnicianId)
                ]),
                $"Incident {Short(dispatched.IncidentId)} dispatched."),

            VisitWorkCompletedIntegrationEvent completed => (
                LiveOperationTypes.VisitWorkCompleted, LiveEntityTypes.Visit, completed.VisitId, LiveOperationActions.Completed,
                Related(routing, [Ref(LiveEntityTypes.Execution, completed.ExecutionId)]),
                $"Work on visit {Short(completed.VisitId)} completed."),

            _ => throw new ArgumentException(
                $"{integrationEvent.GetType().Name} has no live mapping.", nameof(integrationEvent))
        };

        return new LiveOperationMessage(
            envelope.MessageId, type, envelope.OccurredAtUtc, entityType, entityId, action, envelope.CorrelationId, related, summary);
    }

    private static LiveEntityReference Ref(string entityType, Guid entityId) => new(entityType, entityId);

    // The event's own references, then the looked-up technicians and site; each entity once.
    private static IReadOnlyList<LiveEntityReference> Related(LiveRoutingContext routing, IEnumerable<LiveEntityReference> fromEvent) =>
        fromEvent
            .Concat(routing.TechnicianIds.Select(technicianId => Ref(LiveEntityTypes.Technician, technicianId)))
            .Concat(routing.SiteId is { } siteId ? [Ref(LiveEntityTypes.Site, siteId)] : [])
            .Where(reference => reference.EntityId != Guid.Empty)
            .Distinct()
            .ToList();

    private static string Short(Guid id) => id.ToString("N")[..8];
}
