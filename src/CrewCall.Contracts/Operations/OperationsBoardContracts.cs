using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.OperationalCalendar;

namespace CrewCall.Contracts.Operations;

/// <summary>
/// GET /api/operations/board?date=yyyy-MM-dd&amp;timeZoneId=...: the manager's day in one read (Sprint 16). Composed from
/// what already exists — the operational calendar, incidents and their analysis, technicians and their availability —
/// and never stored. Today reuses the calendar item as it is; unassigned visits are the Today items without an
/// assignment.
/// </summary>
/// <param name="Date">The local day shown, in TimeZoneId.</param>
/// <param name="Today">The day's visits, as the operational calendar shows them (same contract).</param>
/// <param name="Incidents">Open incidents (not yet dispatched) and those dispatched for this day; the most urgent first.</param>
/// <param name="Team">Active technicians, with what they are doing now.</param>
/// <param name="Alerts">What needs the manager's attention, most severe first.</param>
public sealed record OperationsBoardResponse(
    DateOnly Date,
    string TimeZoneId,
    DateTimeOffset GeneratedAtUtc,
    OperationalCalendarItemResponse[] Today,
    OperationsBoardIncidentResponse[] Incidents,
    OperationsBoardTechnicianResponse[] Team,
    OperationsBoardAlertResponse[] Alerts);

/// <param name="Analysis">The latest analysis, when the incident was analysed: what a proposal will offer.</param>
public sealed record OperationsBoardIncidentResponse(
    IncidentResponse Incident,
    string CustomerName,
    string SiteName,
    string SiteCity,
    OperationsBoardAnalysisResponse? Analysis);

/// <param name="DirectAssignmentCount">Candidates free for the incident's window: a proposal without any move.</param>
/// <param name="RequiresRescheduleCount">Candidates whose existing work collides: a proposal may move one visit.</param>
public sealed record OperationsBoardAnalysisResponse(
    DateTimeOffset AnalyzedAtUtc,
    int DirectAssignmentCount,
    int RequiresRescheduleCount,
    int UnavailableCount);

/// <param name="State">Available, Traveling, Working, Paused or Unavailable — now.</param>
/// <param name="StateDetail">Why Unavailable (Absence, Holiday, OutsideWorkingHours), or the current visit's work order title.</param>
/// <param name="PhoneNumber">E.164, for a direct call (tel:).</param>
/// <param name="NextVisitStartUtc">The technician's next visit today that has not started.</param>
public sealed record OperationsBoardTechnicianResponse(
    Guid TechnicianId,
    string DisplayName,
    string? PhoneNumber,
    string State,
    string? StateDetail,
    Guid? CurrentVisitId,
    string? CurrentSiteName,
    string? CurrentSiteCity,
    DateTimeOffset? NextVisitStartUtc,
    int VisitsToday);

/// <summary>Something that needs a decision or a call. Computed from the board itself, never stored.</summary>
/// <param name="Kind">
/// IncidentAwaitingDecision, IncidentNeedsReschedule (analysed, every fitting technician is busy), VisitRescheduled
/// (moved today), VisitLate (assigned, should have started, nothing recorded) or VisitUnassigned.
/// </param>
/// <param name="Severity">Critical, Warning or Info.</param>
/// <param name="EntityType">incident or visit.</param>
/// <param name="Subject">Who or where: the incident's title and site, or the visit's work order and site.</param>
/// <param name="At">The time the alert is about: requested start, planned start or the move.</param>
/// <param name="Minutes">Delay of a rescheduled or late visit.</param>
public sealed record OperationsBoardAlertResponse(
    string Kind,
    string Severity,
    string EntityType,
    Guid EntityId,
    string Subject,
    DateTimeOffset At,
    int? Minutes);
