namespace CrewCall.Contracts.Reporting;

// Responses of the read-only Reporting API (ADR-0016). Built from event-driven projections, so eventually consistent:
// every response says when it was generated and how fresh the projections are.

/// <param name="GeneratedAtUtc">When this response was produced.</param>
/// <param name="ProjectionUpdatedAtUtc">When the projections last applied an event; null if none yet.</param>
/// <param name="DataAsOfUtc">The newest business time among the events applied so far; changes after it may still be on
/// their way.</param>
public sealed record ReportMetadata(DateTimeOffset GeneratedAtUtc, DateTimeOffset? ProjectionUpdatedAtUtc, DateTimeOffset? DataAsOfUtc);

/// <summary>One technician's activity on one UTC day. Minutes are actual minutes of completed visits.</summary>
public sealed record TechnicianActivityDay(
    DateOnly Date,
    int CompletedVisits,
    int Assignments,
    int IncidentDispatches,
    decimal TravelMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes);

/// <summary>
/// A technician's activity over an inclusive range of UTC days, with a row per day that has activity (date ascending).
/// OperationalUtilization = NetWorkMinutes / (GrossWorkMinutes + TravelMinutes), null when that is zero. It is not a
/// payroll or contract utilization: there is no data on contracted working time.
/// </summary>
public sealed record TechnicianActivityReport(
    Guid TechnicianId,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int CompletedVisits,
    int AssignmentCount,
    int IncidentDispatchCount,
    decimal TravelMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes,
    decimal? OperationalUtilization,
    IReadOnlyList<TechnicianActivityDay> Daily,
    ReportMetadata Metadata);

public sealed record TechnicianSummaryRequest(IReadOnlyList<Guid>? TechnicianIds, DateOnly? Start, DateOnly? End);

/// <summary>One technician in a summary: the same figures as <see cref="TechnicianActivityReport"/>.</summary>
public sealed record TechnicianActivitySummary(
    Guid TechnicianId,
    int CompletedVisits,
    int AssignmentCount,
    int IncidentDispatchCount,
    decimal TravelMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes,
    decimal? OperationalUtilization,
    IReadOnlyList<TechnicianActivityDay> Daily);

public sealed record TechnicianActivityTotals(
    int TechnicianCount,
    int CompletedVisits,
    int AssignmentCount,
    int IncidentDispatchCount,
    decimal TravelMinutes,
    decimal GrossWorkMinutes,
    decimal PauseMinutes,
    decimal NetWorkMinutes,
    decimal? OperationalUtilization);

/// <summary>Several technicians over one range: one summary each, in request order, and the totals over all of them.</summary>
public sealed record TechnicianSummaryReport(
    DateOnly RangeStart,
    DateOnly RangeEnd,
    IReadOnlyList<TechnicianActivitySummary> Technicians,
    TechnicianActivityTotals Totals,
    ReportMetadata Metadata);

/// <summary>
/// A visit, planned versus actual. PlannedDurationMinutes and VarianceMinutes (ActualNetWorkMinutes −
/// PlannedDurationMinutes) are derived when the report is built, never stored; the variance is null without an actual.
/// Fields an event has not delivered yet are null. RescheduleCount and TotalDelayMinutes say how often, and by how much in
/// total, the visit was moved (visit.rescheduled); the planned window is the current one.
/// </summary>
public sealed record VisitActivityItem(
    Guid VisitId,
    Guid? WorkOrderId,
    Guid? CustomerId,
    Guid? SiteId,
    Guid? TechnicianId,
    DateTimeOffset? PlannedStartUtc,
    DateTimeOffset? PlannedEndUtc,
    decimal? PlannedDurationMinutes,
    decimal? ActualTravelMinutes,
    decimal? ActualGrossWorkMinutes,
    decimal? ActualPauseMinutes,
    decimal? ActualNetWorkMinutes,
    decimal? VarianceMinutes,
    string Status,
    DateTimeOffset? CompletedAtUtc,
    int RescheduleCount,
    int TotalDelayMinutes);

/// <param name="Truncated">True when more visits matched than the report returns.</param>
public sealed record VisitActivityReport(IReadOnlyList<VisitActivityItem> Visits, bool Truncated, ReportMetadata Metadata);
