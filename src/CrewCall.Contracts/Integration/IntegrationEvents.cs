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
