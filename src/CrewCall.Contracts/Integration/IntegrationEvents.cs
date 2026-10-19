namespace CrewCall.Contracts.Integration;

// Version 1 of each published event. A breaking change adds a new version (and type entry); fields are never repurposed.

/// <summary>assignment.created v1: resources were claimed for a visit.</summary>
public sealed record AssignmentCreatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid AssignmentId,
    Guid VisitId,
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds) : IIntegrationEvent;

/// <summary>assignment.replaced v1: the visit's active assignment was replaced by a new one.</summary>
public sealed record AssignmentReplacedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid OldAssignmentId,
    Guid NewAssignmentId,
    Guid VisitId) : IIntegrationEvent;

/// <summary>assignment.cancelled v1: the visit's active assignment was cancelled and its resources released.</summary>
public sealed record AssignmentCancelledIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid AssignmentId,
    Guid VisitId) : IIntegrationEvent;

/// <summary>incident.dispatched v1: an urgent incident became a work order, visit and assignment.</summary>
public sealed record IncidentDispatchedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid IncidentId,
    Guid WorkOrderId,
    Guid VisitId,
    Guid AssignmentId,
    Guid TechnicianId) : IIntegrationEvent;

/// <summary>visit.work-completed v1: field work on a visit was completed, with its actual durations in minutes.</summary>
public sealed record VisitWorkCompletedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid VisitId,
    Guid ExecutionId,
    decimal? TravelMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes) : IIntegrationEvent;

/// <summary>
/// visit.created v1 (Sprint 14): a visit was planned for a work order, with the context read sides need (customer, site,
/// planned window), so they never have to look it up in the operational database.
/// </summary>
/// <param name="WorkOrderPriority">The work order's priority when the visit was created: "Low", "Normal", "High" or "Urgent".</param>
public sealed record VisitCreatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid VisitId,
    Guid WorkOrderId,
    Guid CustomerId,
    Guid SiteId,
    DateTimeOffset PlannedStartUtc,
    DateTimeOffset PlannedEndUtc,
    DateTimeOffset CreatedAtUtc,
    string WorkOrderPriority) : IIntegrationEvent;

/// <summary>
/// visit.status-changed v1 (Sprint 14): a visit moved between statuses ("Planned", "InProgress", "Completed",
/// "Cancelled"). Statuses are strings: the contract does not depend on the domain enum.
/// </summary>
public sealed record VisitStatusChangedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid VisitId,
    string OldStatus,
    string NewStatus) : IIntegrationEvent;

/// <summary>
/// visit.rescheduled v1 (Sprint 15): a visit kept its identity and moved to a new window, e.g. to make room for an
/// urgent incident.
/// </summary>
/// <param name="Reason">Why it moved, e.g. "UrgentIncident".</param>
/// <param name="IncidentId">The urgent incident that displaced it, if any.</param>
public sealed record VisitRescheduledIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid? CorrelationId,
    Guid VisitId,
    DateTimeOffset OldStartUtc,
    DateTimeOffset OldEndUtc,
    DateTimeOffset NewStartUtc,
    DateTimeOffset NewEndUtc,
    string Reason,
    Guid? IncidentId) : IIntegrationEvent;
