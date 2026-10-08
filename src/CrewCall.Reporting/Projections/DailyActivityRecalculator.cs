using CrewCall.Reporting.Data;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Reporting.Projections;

/// <summary>
/// Recomputes technician days from the detail projections (ADR-0016): counts and sums over the technician's visits
/// completed, assignments created and incidents dispatched on that UTC day. A day row is never incremented, so a
/// redelivered or reordered event cannot count twice; a day with nothing left is removed.
/// </summary>
public static class DailyActivityRecalculator
{
    public static async Task RecalculateAsync(
        ReportingDbContext db, IEnumerable<TechnicianDay> days, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var day in days.OrderBy(day => day.TechnicianId).ThenBy(day => day.DateUtc))
        {
            var from = new DateTimeOffset(day.DateUtc.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var to = from.AddDays(1);

            var visits = await db.VisitActivities
                .Where(visit => visit.TechnicianId == day.TechnicianId && visit.CompletedAtUtc >= from && visit.CompletedAtUtc < to)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Count = group.Count(),
                    Travel = group.Sum(visit => visit.ActualTravelMinutes ?? 0),
                    Gross = group.Sum(visit => visit.ActualGrossWorkMinutes ?? 0),
                    Pause = group.Sum(visit => visit.ActualPauseMinutes ?? 0),
                    Net = group.Sum(visit => visit.ActualNetWorkMinutes ?? 0)
                })
                .SingleOrDefaultAsync(cancellationToken);
            var assignments = await db.AssignmentActivities.CountAsync(
                assignment => assignment.TechnicianId == day.TechnicianId && assignment.CreatedAtUtc >= from && assignment.CreatedAtUtc < to,
                cancellationToken);
            var incidents = await db.IncidentActivities.CountAsync(
                incident => incident.TechnicianId == day.TechnicianId && incident.DispatchedAtUtc >= from && incident.DispatchedAtUtc < to,
                cancellationToken);

            var row = await db.TechnicianActivityDays.FindAsync([day.TechnicianId, day.DateUtc], cancellationToken);
            if (visits is null && assignments == 0 && incidents == 0)
            {
                if (row is not null)
                {
                    db.TechnicianActivityDays.Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                row = new TechnicianActivityDaily { TechnicianId = day.TechnicianId, DateUtc = day.DateUtc };
                db.TechnicianActivityDays.Add(row);
            }

            row.CompletedVisits = visits?.Count ?? 0;
            row.TravelMinutes = visits?.Travel ?? 0;
            row.GrossWorkMinutes = visits?.Gross ?? 0;
            row.PauseMinutes = visits?.Pause ?? 0;
            row.NetWorkMinutes = visits?.Net ?? 0;
            row.AssignmentCount = assignments;
            row.IncidentDispatchCount = incidents;
            row.UpdatedAtUtc = now;
        }
    }
}
