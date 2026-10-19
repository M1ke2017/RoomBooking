using CrewCall.Contracts.Integration;
using CrewCall.Scheduling.Assignments;
using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Operations;

namespace CrewCall.Persistence.Messaging;

/// <summary>
/// Decides which operational events become integration events, and builds them (ADR-0014): the explicit mapping table,
/// operational event type and payload → published integration event. Most operational events stay internal history;
/// only explicitly mapped ones are published.
/// </summary>
public static class IntegrationEventMapper
{
    /// <summary>The integration event for this operational event, or null when it is not published.</summary>
    public static IIntegrationEvent? Map(string operationalEventType, DateTimeOffset occurredAtUtc, object payload, Guid? correlationId) =>
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

            (WorkOrderEvents.VisitRescheduled, VisitRescheduledPayload rescheduled) => new VisitRescheduledIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                rescheduled.VisitId, rescheduled.OldStart, rescheduled.OldEnd, rescheduled.NewStart, rescheduled.NewEnd,
                rescheduled.Reason, rescheduled.IncidentId),

            (IncidentEvents.IncidentCreated, IncidentCreatedPayload created) => new IncidentCreatedIntegrationEvent(
                NewId(), occurredAtUtc, correlationId,
                created.IncidentId, created.CustomerId, created.SiteId, created.Priority.ToString(), created.RequestedStart, created.RequestedEnd),

            (VisitExecutionEvents.VisitTravelStarted, VisitTravelStartedPayload travel) =>
                FieldWork(occurredAtUtc, correlationId, travel.VisitId, travel.ExecutionId, FieldWorkStatus.Traveling),
            (VisitExecutionEvents.VisitWorkStarted, VisitWorkStartedPayload started) =>
                FieldWork(occurredAtUtc, correlationId, started.VisitId, started.ExecutionId, FieldWorkStatus.Working),
            (VisitExecutionEvents.VisitWorkPaused, VisitWorkPausedPayload paused) =>
                FieldWork(occurredAtUtc, correlationId, paused.VisitId, paused.ExecutionId, FieldWorkStatus.Paused),
            (VisitExecutionEvents.VisitWorkResumed, VisitWorkResumedPayload resumed) =>
                FieldWork(occurredAtUtc, correlationId, resumed.VisitId, resumed.ExecutionId, FieldWorkStatus.Working),
            (VisitExecutionEvents.VisitExecutionCancelled, VisitExecutionCancelledPayload cancelled) =>
                FieldWork(occurredAtUtc, correlationId, cancelled.VisitId, cancelled.ExecutionId, FieldWorkStatus.Cancelled),

            _ => null
        };

    private static VisitFieldWorkChangedIntegrationEvent FieldWork(
        DateTimeOffset occurredAtUtc, Guid? correlationId, Guid visitId, Guid executionId, FieldWorkStatus status) =>
        new(NewId(), occurredAtUtc, correlationId, visitId, executionId, status.ToString());

    private static Guid NewId() => Guid.CreateVersion7();
}
