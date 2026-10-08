using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Reporting;
using Xunit;
using static CrewCall.Reporting.Tests.Events;

namespace CrewCall.Reporting.Tests;

/// <summary>
/// The read-only Reporting API over HTTP (ADR-0016). The host runs without CrewCall.Api, without an operational
/// database and with the broker unreachable: existing projections are still served.
/// </summary>
[Collection(Sequential.Name)]
public sealed class ReportingApiTests(ReportingInfrastructure infrastructure) : IAsyncLifetime
{
    private static readonly Guid TechnicianA = Guid.NewGuid();
    private static readonly Guid TechnicianB = Guid.NewGuid();
    private static readonly Guid TechnicianC = Guid.NewGuid();
    private static readonly Guid SiteNorth = Guid.NewGuid();
    private static readonly Guid SiteSouth = Guid.NewGuid();
    private static readonly DateOnly D1 = DateOnly.FromDateTime(Day1.UtcDateTime);

    private readonly string _queue = ReportingHosts.NewQueueName();
    private readonly ManualClock _clock = new(Day1.AddDays(10));

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await infrastructure.ResetAsync();
        await using var harness = new ProjectionHarness(infrastructure, _clock);

        // A: two visits on day 1 (north), one on day 2 (south). B: one visit on day 2. C: nothing.
        await harness.ProcessAllAsync(Visit(TechnicianA, SiteNorth, Day1.AddHours(8), 10m, 50m, 5m));
        await harness.ProcessAllAsync(Visit(TechnicianA, SiteNorth, Day1.AddHours(13), null, 70m, 0m));
        await harness.ProcessAllAsync(Visit(TechnicianA, SiteSouth, Day1.AddDays(1).AddHours(9), 30m, 60m, 15m));
        await harness.ProcessAllAsync(Visit(TechnicianB, SiteSouth, Day1.AddDays(1).AddHours(10), 0m, 40m, 0m));
        await harness.ProcessAllAsync(Visit(TechnicianB, SiteSouth, Day1.AddDays(2).AddHours(10), null, null, null)); // planned only
        await harness.ProcessAsync(IncidentDispatched(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TechnicianB, Day1.AddDays(1).AddHours(6)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A planned hour assigned two hours ahead, completed after its planned end when durations are given.</summary>
    private static IEnumerable<CrewCall.Contracts.Integration.IntegrationEventEnvelope> Visit(
        Guid technicianId, Guid siteId, DateTimeOffset start, decimal? travel, decimal? gross, decimal? pause)
    {
        var visitId = Guid.NewGuid();
        yield return Envelope(VisitCreated(visitId, start, TimeSpan.FromHours(1), siteId));
        yield return Envelope(AssignmentCreated(Guid.NewGuid(), visitId, technicianId, start.AddHours(-2)));
        if (gross is { } grossMinutes)
        {
            yield return Envelope(WorkCompleted(visitId, start.AddHours(1), travel, grossMinutes, pause!.Value));
        }
    }

    [Fact]
    public async Task A_technician_report_totals_the_period_with_a_daily_breakdown_utilization_and_freshness_metadata()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        var report = (await client.GetFromJsonAsync<TechnicianActivityReport>(
            $"/api/reporting/technicians/{TechnicianA}?start={D1:yyyy-MM-dd}&end={D1.AddDays(6):yyyy-MM-dd}", Cancellation))!;

        Assert.Equal((TechnicianA, D1, D1.AddDays(6)), (report.TechnicianId, report.RangeStart, report.RangeEnd));
        Assert.Equal((3, 3, 0), (report.CompletedVisits, report.AssignmentCount, report.IncidentDispatchCount));
        Assert.Equal((40m, 180m, 20m, 160m), (report.TravelMinutes, report.GrossWorkMinutes, report.PauseMinutes, report.NetWorkMinutes));
        Assert.Equal(Math.Round(160m / 220m, 4), report.OperationalUtilization);
        Assert.Equal(
            [(D1, 2, 2, 10m, 120m, 5m, 115m), (D1.AddDays(1), 1, 1, 30m, 60m, 15m, 45m)],
            report.Daily.Select(d => (d.Date, d.CompletedVisits, d.Assignments, d.TravelMinutes, d.GrossWorkMinutes, d.PauseMinutes, d.NetWorkMinutes)));
        Assert.NotNull(report.Metadata.ProjectionUpdatedAtUtc);
        Assert.Equal(Day1.AddDays(2).AddHours(8), report.Metadata.DataAsOfUtc); // the newest business time projected
        Assert.True(report.Metadata.GeneratedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5)); // produced now, from projections as of then
    }

    [Fact]
    public async Task A_technician_without_activity_gets_zeros_and_no_utilization_rather_than_an_invented_value()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        var report = (await client.GetFromJsonAsync<TechnicianActivityReport>(
            $"/api/reporting/technicians/{TechnicianC}?start={D1:yyyy-MM-dd}&end={D1:yyyy-MM-dd}", Cancellation))!;

        Assert.Equal((0, 0m, (decimal?)null), (report.CompletedVisits, report.NetWorkMinutes, report.OperationalUtilization));
        Assert.Empty(report.Daily);
    }

    [Fact]
    public async Task A_summary_reports_each_technician_in_request_order_with_daily_rows_and_the_totals()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/reporting/technicians/summary",
            new TechnicianSummaryRequest([TechnicianB, TechnicianA, TechnicianC, TechnicianA], D1, D1.AddDays(30)), Cancellation);
        var summary = (await response.Content.ReadFromJsonAsync<TechnicianSummaryReport>(Cancellation))!;

        Assert.Equal([TechnicianB, TechnicianA, TechnicianC], summary.Technicians.Select(t => t.TechnicianId));
        var b = summary.Technicians[0];
        Assert.Equal((1, 2, 1, 40m), (b.CompletedVisits, b.AssignmentCount, b.IncidentDispatchCount, b.NetWorkMinutes));
        Assert.Equal([D1.AddDays(1), D1.AddDays(2)], b.Daily.Select(d => d.Date)); // day 2: work, incident; day 3: the planned visit's assignment
        Assert.Equal((3, 160m), (summary.Technicians[1].CompletedVisits, summary.Technicians[1].NetWorkMinutes));
        Assert.Empty(summary.Technicians[2].Daily);

        Assert.Equal((3, 4, 5, 1, 40m, 220m, 20m, 200m), (summary.Totals.TechnicianCount, summary.Totals.CompletedVisits, summary.Totals.AssignmentCount,
            summary.Totals.IncidentDispatchCount, summary.Totals.TravelMinutes, summary.Totals.GrossWorkMinutes, summary.Totals.PauseMinutes, summary.Totals.NetWorkMinutes));
        Assert.Equal(Math.Round(200m / 260m, 4), summary.Totals.OperationalUtilization);
        Assert.All(summary.Technicians, t => Assert.Equal(t.Daily.OrderBy(d => d.Date).Select(d => d.Date), t.Daily.Select(d => d.Date)));
    }

    [Fact]
    public async Task The_visit_report_filters_by_technician_site_period_and_status_and_derives_planned_versus_actual()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        async Task<VisitActivityReport> Get(string query) =>
            (await client.GetFromJsonAsync<VisitActivityReport>($"/api/reporting/visits?{query}", Cancellation))!;

        var byTechnician = await Get($"technicianId={TechnicianA}");
        Assert.Equal(3, byTechnician.Visits.Count);
        Assert.Equal(byTechnician.Visits.OrderBy(v => v.PlannedStartUtc).Select(v => v.VisitId), byTechnician.Visits.Select(v => v.VisitId));
        var first = byTechnician.Visits[0];
        Assert.Equal((60m, 45m, -15m, "Completed", SiteNorth), (first.PlannedDurationMinutes, first.ActualNetWorkMinutes, first.VarianceMinutes, first.Status, first.SiteId));

        Assert.Equal(3, (await Get($"siteId={SiteSouth}")).Visits.Count);
        Assert.Equal(2, (await Get($"siteId={SiteSouth}&start={D1.AddDays(1):yyyy-MM-dd}&end={D1.AddDays(1):yyyy-MM-dd}")).Visits.Count);
        var planned = Assert.Single((await Get($"technicianId={TechnicianB}&status=Planned")).Visits);
        Assert.Equal(((decimal?)null, (decimal?)null, 60m), (planned.ActualNetWorkMinutes, planned.VarianceMinutes, planned.PlannedDurationMinutes));
        Assert.False((await Get("")).Truncated);
    }

    [Theory]
    [InlineData("start=2038-06-07", "end")]
    [InlineData("start=2038-06-07&end=2038-06-06", "end")]
    [InlineData("start=2038-01-01&end=2039-01-02", "end")]
    public async Task Missing_reversed_or_too_long_ranges_are_refused(string query, string field)
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/api/reporting/technicians/{TechnicianA}?{query}", Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task A_summary_needs_one_to_a_hundred_technicians_and_366_days_is_the_longest_range()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        async Task<HttpStatusCode> Post(IReadOnlyList<Guid>? ids, DateOnly start, DateOnly end)
        {
            using var response = await client.PostAsJsonAsync("/api/reporting/technicians/summary", new TechnicianSummaryRequest(ids, start, end), Cancellation);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Post([], D1, D1));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(null, D1, D1));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList(), D1, D1));
        Assert.Equal(HttpStatusCode.OK, await Post(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList(), D1, D1.AddDays(365)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post([TechnicianA], D1, D1.AddDays(366)));
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_the_broker_and_readiness_reports_it()
    {
        await using var host = ReportingHosts.Reporting(infrastructure, _queue, ReportingHosts.UnreachableBroker);
        using var client = host.CreateClient();

        using var alive = await client.GetAsync("/alive", Cancellation);
        using var health = await client.GetAsync("/health", Cancellation);
        var body = await health.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Contains("\"name\":\"rabbitmq\",\"status\":\"Unhealthy\"", body);
        Assert.Contains("\"name\":\"database\",\"status\":\"Healthy\"", body);
    }
}
