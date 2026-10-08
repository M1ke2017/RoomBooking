using CrewCall.Contracts.Integration;
using CrewCall.Scheduling.Assignments;
using CrewCall.WorkOrders.Operations;

namespace CrewCall.Persistence.Messaging;

/// <summary>
/// Decides which operational events become integration events, and builds them (ADR-0014). Most operational events stay
/// internal history; only explicitly mapped ones are published.
/// </summary>
public interface IIntegrationEventMapper
{
    /// <summary>The integration event for this operational event, or null when it is not published.</summary>
    IIntegrationEvent? Map(string operationalEventType, DateTimeOffset occurredAtUtc, object payload, Guid? correlationId);
}

/// <summary>The explicit mapping table: operational event type and payload → published integration event.</summary>
public sealed class IntegrationEventMapper : IIntegrationEventMapper
{
    public IIntegrationEvent? Map(string operationalEventType, DateTimeOffset occurredAtUtc, object payload, Guid? correlationId) =>
        (operationalEventType, payload) switch
        {
            (AssignmentEvents.AssignmentCreated, AssignmentCreatedPayload created) => new AssignmentCreatedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                created.AssignmentId, created.VisitId, created.TechnicianId, created.VehicleId, created.EquipmentIds.ToList()),

            (AssignmentEvents.AssignmentReplaced, AssignmentReplacedPayload replaced) => new AssignmentReplacedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId, replaced.OldAssignmentId, replaced.NewAssignmentId, replaced.VisitId),

            (AssignmentEvents.AssignmentCancelled, AssignmentCancelledPayload cancelled) => new AssignmentCancelledIntegrationEvent(
                NewId(), occurredAtUtc, correlationId, cancelled.AssignmentId, cancelled.VisitId),

            (IncidentEvents.IncidentDispatched, IncidentDispatchedPayload dispatched) => new IncidentDispatchedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                dispatched.IncidentId, dispatched.WorkOrderId, dispatched.VisitId, dispatched.AssignmentId, dispatched.TechnicianId),

            (VisitExecutionEvents.VisitWorkCompleted, VisitWorkCompletedPayload completed) => new VisitWorkCompletedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                completed.VisitId, completed.ExecutionId, completed.TravelDurationMinutes,
                completed.GrossWorkMinutes, completed.PauseMinutes, completed.NetWorkMinutes),

            (WorkOrderEvents.VisitCreated, VisitCreatedPayload created) => new VisitCreatedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                created.VisitId, created.WorkOrderId, created.CustomerId, created.SiteId, created.Start, created.End, occurredAtUtc,
                created.Priority.ToString()),

            (WorkOrderEvents.VisitStatusChanged, VisitStatusChangedPayload changed) => new VisitStatusChangedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId, changed.VisitId, changed.OldStatus.ToString(), changed.NewStatus.ToString()),

            _ => null
        };

    private static Guid NewId() => Guid.CreateVersion7();
}
