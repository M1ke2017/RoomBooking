namespace CrewCall.Reporting.Data;

// The reporting read model (ADR-0016). These are projections of integration events, owned by this service and stored
// in its own database. They are not the operational domain: only what the reports need, nullable wherever an event that
// fills a field may not have arrived yet (delivery order is not guaranteed).

/// <summary>A message the reporting consumer has projected (reporting.inbox_messages): its idempotency record.</summary>
public sealed class ReportingInboxMessage
{
    public const int ConsumerNameMaxLength = 100;
    public const int TypeMaxLength = 100;

    public required string ConsumerName { get; init; }

    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

    public required int Version { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }
}

/// <summary>
/// One technician's activity on one UTC day (reporting.technician_activity), recomputed from the detail projections
/// whenever one of them changes for that technician and day. Never incremented in place.
/// </summary>
public sealed class TechnicianActivityDaily
{
    public required Guid TechnicianId { get; init; }

    public required DateOnly DateUtc { get; init; }

    public int CompletedVisits { get; set; }

    public int AssignmentCount { get; set; }

    public int IncidentDispatchCount { get; set; }

    public decimal TravelMinutes { get; set; }

    public decimal GrossWorkMinutes { get; set; }

    public decimal PauseMinutes { get; set; }

    public decimal NetWorkMinutes { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A visit, planned and actual (reporting.visit_activity).</summary>
public sealed class VisitActivity
{
    public const int StatusMaxLength = 20;
    public const int PriorityMaxLength = 20;
    public const string UnknownStatus = "Unknown";

    public required Guid VisitId { get; init; }

    public Guid? WorkOrderId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? SiteId { get; set; }

    public string? WorkOrderPriority { get; set; }

    /// <summary>The technician the visit is attributed to: the technician of its current assignment, if known.</summary>
    public Guid? TechnicianId { get; set; }

    public DateTimeOffset? PlannedStartUtc { get; set; }

    public DateTimeOffset? PlannedEndUtc { get; set; }

    public Guid? ExecutionId { get; set; }

    public decimal? ActualTravelMinutes { get; set; }

    public decimal? ActualGrossWorkMinutes { get; set; }

    public decimal? ActualPauseMinutes { get; set; }

    public decimal? ActualNetWorkMinutes { get; set; }

    public string VisitStatus { get; set; } = UnknownStatus;

    /// <summary>When the business change that set <see cref="VisitStatus"/> happened: the latest one wins.</summary>
    public DateTimeOffset? StatusChangedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public static class AssignmentActivityStatus
{
    public const string Active = "Active";
    public const string Replaced = "Replaced";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// An assignment and how it ended (reporting.assignment_activity). Rows are never deleted: a replaced assignment stays,
/// so the history says the visit was assigned to A and then to B.
/// </summary>
public sealed class AssignmentActivity
{
    public const int StatusMaxLength = 20;

    public required Guid AssignmentId { get; init; }

    public required Guid VisitId { get; init; }

    /// <summary>Null until assignment.created (or incident.dispatched) arrives, e.g. after an earlier assignment.replaced.</summary>
    public Guid? TechnicianId { get; set; }

    public Guid? VehicleId { get; set; }

    public DateTimeOffset? CreatedAtUtc { get; set; }

    public string Status { get; set; } = AssignmentActivityStatus.Active;

    public DateTimeOffset? EndedAtUtc { get; set; }

    public Guid? ReplacedByAssignmentId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>An urgent incident's dispatch (reporting.incident_activity).</summary>
public sealed class IncidentActivity
{
    public required Guid IncidentId { get; init; }

    public Guid WorkOrderId { get; set; }

    public Guid VisitId { get; set; }

    public Guid AssignmentId { get; set; }

    public Guid TechnicianId { get; set; }

    public DateTimeOffset DispatchedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// How far a projection has got (reporting.projection_checkpoints): the newest business time it has seen and when it
/// last applied a message. Reports return it so readers can see how fresh the data is.
/// </summary>
public sealed class ProjectionCheckpoint
{
    public const int NameMaxLength = 100;

    public required string ProjectionName { get; init; }

    public DateTimeOffset? LastEventOccurredAtUtc { get; set; }

    public DateTimeOffset? LastProcessedAtUtc { get; set; }

    public long ProcessedMessages { get; set; }
}
