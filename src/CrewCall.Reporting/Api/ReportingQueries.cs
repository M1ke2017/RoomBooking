using CrewCall.Contracts.Reporting;
using CrewCall.Reporting.Data;
using CrewCall.Reporting.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrewCall.Reporting.Api;

/// <summary>Filters of the visit report. Dates are UTC days of the planned start, inclusive.</summary>
public sealed record VisitReportFilter(Guid? TechnicianId, Guid? SiteId, DateOnly? Start, DateOnly? End, string? Status);

/// <summary>
/// Read-only queries over the reporting projections (ADR-0016). Reports aggregate the daily technician rows, so a month
/// or a year is a scan of at most one row per technician and day, never of the event history.
/// </summary>
public sealed class ReportingQueries(ReportingDbContext db, TimeProvider clock, IOptions<ReportingOptions> options, ILogger<ReportingQueries> logger)
{
    public async Task<TechnicianActivityReport> TechnicianAsync(Guid technicianId, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var days = await DaysAsync([technicianId], start, end, cancellationToken);
        var daily = days.Select(ToDay).ToList();
        logger.LogInformation("Technician report {TechnicianId} {Start}..{End}: {Days} days with activity.", technicianId, start, end, daily.Count);

        return new TechnicianActivityReport(
            technicianId, start, end,
            daily.Sum(day => day.CompletedVisits),
            daily.Sum(day => day.Assignments),
            daily.Sum(day => day.IncidentDispatches),
            daily.Sum(day => day.TravelMinutes),
            daily.Sum(day => day.GrossWorkMinutes),
            daily.Sum(day => day.PauseMinutes),
            daily.Sum(day => day.NetWorkMinutes),
            OperationalUtilization(daily),
            daily,
            await MetadataAsync(cancellationToken));
    }

    public async Task<TechnicianSummaryReport> SummaryAsync(
        IReadOnlyList<Guid> technicianIds, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var byTechnician = (await DaysAsync(technicianIds, start, end, cancellationToken)).ToLookup(row => row.TechnicianId);
        var technicians = technicianIds
            .Select(technicianId =>
            {
                var daily = byTechnician[technicianId].Select(ToDay).ToList();
                return new TechnicianActivitySummary(
                    technicianId,
                    daily.Sum(day => day.CompletedVisits),
                    daily.Sum(day => day.Assignments),
                    daily.Sum(day => day.IncidentDispatches),
                    daily.Sum(day => day.TravelMinutes),
                    daily.Sum(day => day.GrossWorkMinutes),
                    daily.Sum(day => day.PauseMinutes),
                    daily.Sum(day => day.NetWorkMinutes),
                    OperationalUtilization(daily),
                    daily);
            })
            .ToList();
        var allDays = technicians.SelectMany(technician => technician.Daily).ToList();
        logger.LogInformation(
            "Technician summary for {TechnicianCount} technicians {Start}..{End}: {Days} technician days.", technicians.Count, start, end, allDays.Count);

        var totals = new TechnicianActivityTotals(
            technicians.Count,
            technicians.Sum(t => t.CompletedVisits),
            technicians.Sum(t => t.AssignmentCount),
            technicians.Sum(t => t.IncidentDispatchCount),
            technicians.Sum(t => t.TravelMinutes),
            technicians.Sum(t => t.GrossWorkMinutes),
            technicians.Sum(t => t.PauseMinutes),
            technicians.Sum(t => t.NetWorkMinutes),
            OperationalUtilization(allDays));
        return new TechnicianSummaryReport(start, end, technicians, totals, await MetadataAsync(cancellationToken));
    }

    public async Task<VisitActivityReport> VisitsAsync(VisitReportFilter filter, CancellationToken cancellationToken)
    {
        var visits = db.VisitActivities.AsNoTracking();
        if (filter.TechnicianId is { } technicianId)
        {
            visits = visits.Where(visit => visit.TechnicianId == technicianId);
        }

        if (filter.SiteId is { } siteId)
        {
            visits = visits.Where(visit => visit.SiteId == siteId);
        }

        if (filter.Start is { } start)
        {
            var from = StartOf(start);
            visits = visits.Where(visit => visit.PlannedStartUtc >= from);
        }

        if (filter.End is { } end)
        {
            var to = StartOf(end).AddDays(1);
            visits = visits.Where(visit => visit.PlannedStartUtc < to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            visits = visits.Where(visit => visit.VisitStatus == filter.Status);
        }

        var limit = options.Value.MaxVisitRows;
        var rows = await visits
            .OrderBy(visit => visit.PlannedStartUtc == null)
            .ThenBy(visit => visit.PlannedStartUtc)
            .ThenBy(visit => visit.VisitId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        logger.LogInformation(
            "Visit report technician {TechnicianId} site {SiteId} {Start}..{End} status {Status}: {Count} visits.",
            filter.TechnicianId, filter.SiteId, filter.Start, filter.End, filter.Status, Math.Min(rows.Count, limit));

        return new VisitActivityReport(rows.Take(limit).Select(ToItem).ToList(), rows.Count > limit, await MetadataAsync(cancellationToken));
    }

    /// <summary>
    /// NetWorkMinutes / (GrossWorkMinutes + TravelMinutes): the share of the time on completed visits (travel plus time
    /// on site) that was actual work. Null when there is no such time: no data is not zero utilization.
    /// </summary>
    public static decimal? OperationalUtilization(IReadOnlyCollection<TechnicianActivityDay> days)
    {
        var denominator = days.Sum(day => day.GrossWorkMinutes + day.TravelMinutes);
        return denominator == 0 ? null : Math.Round(days.Sum(day => day.NetWorkMinutes) / denominator, 4);
    }

    /// <summary>Planned versus actual, derived here: never stored.</summary>
    public static VisitActivityItem ToItem(VisitActivity visit)
    {
        decimal? planned = visit is { PlannedStartUtc: { } start, PlannedEndUtc: { } end }
            ? (decimal)(end - start).TotalMinutes
            : null;
        decimal? variance = visit.ActualNetWorkMinutes is { } actual && planned is { } plannedMinutes ? actual - plannedMinutes : null;

        return new VisitActivityItem(
            visit.VisitId, visit.WorkOrderId, visit.CustomerId, visit.SiteId, visit.TechnicianId,
            visit.PlannedStartUtc, visit.PlannedEndUtc, planned,
            visit.ActualTravelMinutes, visit.ActualGrossWorkMinutes, visit.ActualPauseMinutes, visit.ActualNetWorkMinutes,
            variance, visit.VisitStatus, visit.CompletedAtUtc, visit.RescheduleCount, visit.TotalDelayMinutes);
    }

    private Task<List<TechnicianActivityDaily>> DaysAsync(
        IReadOnlyList<Guid> technicianIds, DateOnly start, DateOnly end, CancellationToken cancellationToken) =>
        db.TechnicianActivityDays.AsNoTracking()
            .Where(row => technicianIds.Contains(row.TechnicianId) && row.DateUtc >= start && row.DateUtc <= end)
            .OrderBy(row => row.TechnicianId)
            .ThenBy(row => row.DateUtc)
            .ToListAsync(cancellationToken);

    private static TechnicianActivityDay ToDay(TechnicianActivityDaily row) => new(
        row.DateUtc, row.CompletedVisits, row.AssignmentCount, row.IncidentDispatchCount,
        row.TravelMinutes, row.GrossWorkMinutes, row.PauseMinutes, row.NetWorkMinutes);

    private async Task<ReportMetadata> MetadataAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await db.ProjectionCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectionName == ReportingProjectionProcessor.ProjectionName, cancellationToken);
        return new ReportMetadata(clock.GetUtcNow(), checkpoint?.LastProcessedAtUtc, checkpoint?.LastEventOccurredAtUtc);
    }

    private static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
