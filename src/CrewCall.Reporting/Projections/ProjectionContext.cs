using CrewCall.Reporting.Data;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Reporting.Projections;

/// <summary>A technician's UTC day: the key of one <see cref="TechnicianActivityDaily"/> row.</summary>
public readonly record struct TechnicianDay(Guid TechnicianId, DateOnly DateUtc)
{
    public static TechnicianDay? Of(Guid? technicianId, DateTimeOffset? at) =>
        technicianId is { } technician && at is { } instant ? new TechnicianDay(technician, DateOnly.FromDateTime(instant.UtcDateTime)) : null;
}

/// <summary>
/// One message's projection work, inside its transaction. Loads (or starts) the detail rows a handler changes and
/// remembers the technician days each row counted towards before and after the change, so exactly those daily rows are
/// recomputed (<see cref="DailyActivityRecalculator"/>).
/// </summary>
public sealed class ProjectionContext(ReportingDbContext db, DateTimeOffset now)
{
    private readonly HashSet<TechnicianDay> _daysBefore = [];
    private readonly Dictionary<Guid, VisitActivity> _visits = [];
    private readonly Dictionary<Guid, AssignmentActivity> _assignments = [];
    private readonly Dictionary<Guid, IncidentActivity> _incidents = [];

    public DateTimeOffset Now { get; } = now;

    /// <summary>The visit's row, started empty if no event about it arrived yet.</summary>
    public async Task<VisitActivity> VisitAsync(Guid visitId, CancellationToken cancellationToken)
    {
        if (_visits.TryGetValue(visitId, out var known))
        {
            return known;
        }

        var visit = await db.VisitActivities.FindAsync([visitId], cancellationToken);
        if (visit is null)
        {
            visit = new VisitActivity { VisitId = visitId };
            db.VisitActivities.Add(visit);
        }

        Remember(_visits, visitId, visit, CompletedDay(visit));
        return visit;
    }

    /// <summary>The assignment's row, started (Active, technician unknown) if no event about it arrived yet.</summary>
    public async Task<AssignmentActivity> AssignmentAsync(Guid assignmentId, Guid visitId, CancellationToken cancellationToken)
    {
        if (_assignments.TryGetValue(assignmentId, out var known))
        {
            return known;
        }

        var assignment = await db.AssignmentActivities.FindAsync([assignmentId], cancellationToken);
        if (assignment is null)
        {
            assignment = new AssignmentActivity { AssignmentId = assignmentId, VisitId = visitId };
            db.AssignmentActivities.Add(assignment);
        }

        Remember(_assignments, assignmentId, assignment, AssignedDay(assignment));
        return assignment;
    }

    public async Task<IncidentActivity> IncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (_incidents.TryGetValue(incidentId, out var known))
        {
            return known;
        }

        var incident = await db.IncidentActivities.FindAsync([incidentId], cancellationToken);
        if (incident is null)
        {
            incident = new IncidentActivity { IncidentId = incidentId };
            db.IncidentActivities.Add(incident);
        }

        Remember(_incidents, incidentId, incident, DispatchedDay(incident));
        return incident;
    }

    /// <summary>
    /// Attributes the visit to the technician of its current assignment, as far as the assignments seen so far tell
    /// (ADR-0016): the assignment that was neither replaced nor cancelled; among several (a replacement whose
    /// assignment.replaced has not arrived yet), the newest, and one whose creation is not known yet counts as newest.
    /// Order-independent: once all assignment events arrived, the result is the same whatever their order.
    /// </summary>
    public async Task RefreshVisitAttributionAsync(Guid visitId, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        var visit = _visits.GetValueOrDefault(visitId) ?? await db.VisitActivities.FindAsync([visitId], cancellationToken);
        if (visit is null)
        {
            return; // the visit's own events will attribute it when they arrive
        }

        var candidates = await db.AssignmentActivities
            .Where(assignment => assignment.VisitId == visitId
                && assignment.ReplacedByAssignmentId == null
                && assignment.Status != AssignmentActivityStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var current = candidates
            .OrderByDescending(assignment => assignment.CreatedAtUtc is null)
            .ThenByDescending(assignment => assignment.CreatedAtUtc)
            .ThenByDescending(assignment => assignment.AssignmentId)
            .FirstOrDefault();

        if (visit.TechnicianId != current?.TechnicianId)
        {
            visit = await VisitAsync(visitId, cancellationToken); // remembers the day it counted towards before
            visit.TechnicianId = current?.TechnicianId;
            visit.UpdatedAtUtc = Now;
        }
    }

    /// <summary>Every technician day a changed row counted towards before or counts towards now.</summary>
    public IReadOnlyCollection<TechnicianDay> AffectedDays()
    {
        var days = new HashSet<TechnicianDay>(_daysBefore);
        foreach (var day in _visits.Values.Select(CompletedDay)
                     .Concat(_assignments.Values.Select(AssignedDay))
                     .Concat(_incidents.Values.Select(DispatchedDay)))
        {
            if (day is { } known)
            {
                days.Add(known);
            }
        }

        return days;
    }

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    // A visit counts towards the day it was completed, for the technician it is attributed to.
    internal static TechnicianDay? CompletedDay(VisitActivity visit) => TechnicianDay.Of(visit.TechnicianId, visit.CompletedAtUtc);

    // An assignment counts towards the day it was created, for its technician.
    internal static TechnicianDay? AssignedDay(AssignmentActivity assignment) => TechnicianDay.Of(assignment.TechnicianId, assignment.CreatedAtUtc);

    internal static TechnicianDay? DispatchedDay(IncidentActivity incident) =>
        incident.TechnicianId == Guid.Empty ? null : TechnicianDay.Of(incident.TechnicianId, incident.DispatchedAtUtc);

    private void Remember<T>(Dictionary<Guid, T> rows, Guid id, T row, TechnicianDay? dayBefore)
    {
        rows[id] = row;
        if (dayBefore is { } day)
        {
            _daysBefore.Add(day);
        }
    }
}
