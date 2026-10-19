using System.Net;
using System.Net.Http.Json;
using CrewCall.Api.Operations;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Operations;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Technicians;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkOrders;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>
/// The manager's operations board (Sprint 16): one read with today's plan, the urgent incidents, the team and the alerts.
/// The shared test database holds other tests' data too, so each test looks only at its own technicians, visits and
/// incidents, on its own day.
/// </summary>
public sealed class OperationsBoardTests(CrewCallApiFactory factory)
{
    [Fact]
    public async Task The_board_shows_the_days_plan_the_incidents_the_team_and_what_needs_attention()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("BD");
        var skill = await CreateSkillAsync(client, code);
        var working = await TechnicianAsync(client, "+48 601 000 001", skill);
        var free = await TechnicianAsync(client, null, skill);
        var absent = await TechnicianAsync(client, null, skill);
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{absent}/absences",
            new CreateAbsenceRequest(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2), "SickLeave", null));

        // Today: one visit in progress (Working), one assigned for later, one nobody has been assigned to.
        var inProgress = await ExistingWorkAsync(client, working, day.AddHours(8), day.AddHours(10));
        foreach (var step in new[] { "start-travel", "start-work" })
        {
            (await client.PostAsync($"/api/visits/{inProgress.Visit.Id}/execution/{step}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        var later = await ExistingWorkAsync(client, free, day.AddHours(12), day.AddHours(13));
        var unassignedWorkOrder = await ApiTestData.CreateWorkOrderAsync(client);
        var unassigned = await PostAsync<VisitResponse>(client, $"/api/work-orders/{unassignedWorkOrder.Id}/visits",
            new CreateVisitRequest(day.AddHours(14), day.AddHours(15), null));

        // Two incidents: one new, one analysed where every fitting technician is busy.
        var fresh = await CreateIncidentAsync(client, day.AddHours(16), day.AddHours(17), "Urgent", code);
        var conflicted = await CreateIncidentAsync(client, day.AddHours(9), day.AddHours(10), "Critical", code);
        await AnalyzeAsync(client, conflicted.Id, working);

        var board = await BoardAsync(client, day);

        // Today: our three visits in time order, as the calendar shows them.
        Assert.Equal((DateOnly.FromDateTime(day.UtcDateTime), "UTC"), (board.Date, board.TimeZoneId));
        var mine = board.Today.Where(item => item.VisitId == inProgress.Visit.Id || item.VisitId == later.Visit.Id || item.VisitId == unassigned.Id).ToList();
        Assert.Equal([inProgress.Visit.Id, later.Visit.Id, unassigned.Id], mine.Select(item => item.VisitId));
        Assert.Equal(("Working", (Guid?)working, "Technician"), (mine[0].FieldWorkStatus, mine[0].TechnicianId, mine[0].TechnicianName));
        Assert.Null(mine[2].AssignmentId);

        // Incidents: open ones first, most urgent first, with their latest analysis.
        var incidents = board.Incidents.Where(entry => entry.Incident.Id == fresh.Id || entry.Incident.Id == conflicted.Id).ToList();
        Assert.Equal([conflicted.Id, fresh.Id], incidents.Select(entry => entry.Incident.Id));
        Assert.Equal(("Plant 1", "Poznań"), (incidents[0].SiteName, incidents[0].SiteCity));
        Assert.Equal((0, 1), (incidents[0].Analysis!.DirectAssignmentCount, incidents[0].Analysis!.RequiresRescheduleCount));
        Assert.Null(incidents[1].Analysis);

        // Team: what each one is doing now, and how to reach them.
        var team = board.Team.Where(member => member.TechnicianId == working || member.TechnicianId == free || member.TechnicianId == absent)
            .ToDictionary(member => member.TechnicianId);
        Assert.Equal(("Working", (Guid?)inProgress.Visit.Id, "Plant 1", "+48601000001", 1),
            (team[working].State, team[working].CurrentVisitId, team[working].CurrentSiteName, team[working].PhoneNumber, team[working].VisitsToday));
        Assert.Equal(("Available", (DateTimeOffset?)day.AddHours(12)), (team[free].State, team[free].NextVisitStartUtc));
        Assert.Equal(("Unavailable", "Absence"), (team[absent].State, team[absent].StateDetail));

        // Alerts: the incidents waiting for a decision (one needs a reschedule) and the unassigned visit.
        var alerts = board.Alerts.Where(alert => alert.EntityId == fresh.Id || alert.EntityId == conflicted.Id || alert.EntityId == unassigned.Id).ToList();
        Assert.Equal(
            [("IncidentNeedsReschedule", "Critical", conflicted.Id), ("IncidentAwaitingDecision", "Critical", fresh.Id), ("VisitUnassigned", "Warning", unassigned.Id)],
            alerts.Select(alert => (alert.Kind, alert.Severity, alert.EntityId)));
    }

    [Fact]
    public async Task A_dispatched_incident_stays_on_its_day_and_leaves_the_alerts()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);
        var incident = await ReadyIncidentAsync(client, day, [technician]);
        (await DispatchAsync(client, incident.Id, Dispatch(technician))).EnsureSuccessStatusCode();

        var board = await BoardAsync(client, day);
        var other = await BoardAsync(client, day.AddDays(1));

        var shown = Assert.Single(board.Incidents, entry => entry.Incident.Id == incident.Id);
        Assert.Equal("Dispatched", shown.Incident.Status);
        Assert.DoesNotContain(board.Alerts, alert => alert.EntityId == incident.Id);
        Assert.Contains(board.Today, item => item.TechnicianId == technician && item.VisitStartUtc == day.AddHours(10));
        Assert.DoesNotContain(other.Incidents, entry => entry.Incident.Id == incident.Id);
    }

    [Fact]
    public void Late_and_rescheduled_visits_raise_alerts_and_finished_work_does_not()
    {
        var now = new DateTimeOffset(2038, 6, 7, 12, 0, 0, TimeSpan.Zero);
        OperationalCalendarItemResponse Visit(DateTimeOffset start, string status, string fieldWork, Guid? assignmentId, OperationalCalendarRescheduleResponse? moved = null) =>
            new(Guid.NewGuid(), start, start.AddHours(1), start, start.AddHours(1), status, Guid.NewGuid(), "Inspection", "Normal", "Planned",
                Guid.NewGuid(), "ABC", Guid.NewGuid(), "Plant", "Tychy", assignmentId, assignmentId is null ? null : "Active",
                null, null, null, null, null, null, null, 0, 0, [], fieldWork, null, null, null, null, null, null, moved);

        var late = Visit(now.AddMinutes(-40), "Planned", "NotStarted", Guid.NewGuid());
        var justDue = Visit(now.AddMinutes(-10), "Planned", "NotStarted", Guid.NewGuid());
        var moved = Visit(now.AddHours(2), "Planned", "NotStarted", Guid.NewGuid(),
            new OperationalCalendarRescheduleResponse(now, now.AddHours(1), now.AddHours(2), now.AddHours(3), "UrgentIncident", Guid.NewGuid(), now.AddMinutes(-5), 120));
        var done = Visit(now.AddHours(-3), "Completed", "Completed", Guid.NewGuid());
        var started = Visit(now.AddMinutes(-40), "InProgress", "Working", Guid.NewGuid());

        var alerts = OperationsBoardService.Alerts([late, justDue, moved, done, started], [], now);

        Assert.Equal([("VisitLate", late.VisitId, (int?)40), ("VisitRescheduled", moved.VisitId, 120)],
            alerts.Select(alert => (alert.Kind, alert.EntityId, alert.Minutes)));
    }

    [Theory]
    [InlineData("date=2038-13-01")]
    [InlineData("date=07.06.2038")]
    [InlineData("timeZoneId=Mars/Olympus")]
    public async Task An_invalid_date_or_time_zone_returns_400(string query)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/operations/board?{query}", Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_date_the_board_shows_today_in_Warsaw()
    {
        using var client = factory.CreateClient();

        var board = await GetAsync<OperationsBoardResponse>(client, "/api/operations/board");

        Assert.Equal("Europe/Warsaw", board.TimeZoneId);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime), board.Date);
    }

    internal static Task<OperationsBoardResponse> BoardAsync(HttpClient client, DateTimeOffset day) =>
        GetAsync<OperationsBoardResponse>(client, $"/api/operations/board?date={day:yyyy-MM-dd}&timeZoneId=UTC");

    private static async Task<Guid> TechnicianAsync(HttpClient client, string? phone, Guid skill)
    {
        var technician = await PostAsync<TechnicianResponse>(client, "/api/technicians",
            new CreateTechnicianRequest("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", null, "Europe/Warsaw", "PL", phone));
        foreach (var weekday in Enum.GetNames<DayOfWeek>())
        {
            await PostAsync<Contracts.WorkingHours.WorkingHoursResponse>(client, $"/api/technicians/{technician.Id}/working-hours",
                new Contracts.WorkingHours.CreateWorkingHoursRequest(weekday, "00:00", "24:00"));
        }

        (await client.PostAsync($"/api/technicians/{technician.Id}/skills/{skill}", null, Cancellation)).EnsureSuccessStatusCode();
        return technician.Id;
    }
}
