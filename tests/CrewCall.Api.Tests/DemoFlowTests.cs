using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrewCall.Api.Demo;
using CrewCall.Contracts.Incidents;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CrewCall.Api.Tests.IncidentTestKit;

namespace CrewCall.Api.Tests;

/// <summary>
/// The portfolio demo (Sprint 16), through the API only, as the /operations screen drives it: the seeded day → the
/// board → analyze → proposal → the manager accepts → the board, calendar and history show the new plan. The clock is
/// fixed on a day no other test uses: Tuesday 2041-03-12, 11:07 in Warsaw.
/// </summary>
public sealed class DemoFlowTests(CrewCallApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2041, 3, 12, 11, 7, 0, TimeSpan.FromHours(1));

    [Fact]
    public async Task The_demo_day_shows_a_conflict_the_manager_resolves_with_one_approved_reschedule()
    {
        await using var host = DemoHost();
        using var client = host.CreateClient(); // starts the host: the demo plan is seeded at startup

        // 1. The plan of the day, as the manager opens /operations.
        var board = await BoardAsync(client);
        var demo = Demo(board);
        Assert.Equal(5, demo.Count);
        Assert.Equal(["Completed", "Working", "NotStarted", "NotStarted", "NotStarted"], demo.Select(item => item.FieldWorkStatus));
        var inspection = Assert.Single(demo, item => item.WorkOrderTitle == "Przegląd instalacji elektrycznej");
        Assert.Equal((Local(12, 15), Local(13, 45), "Jan Kowalski", "ST 1234A"),
            (inspection.VisitStartUtc, inspection.VisitEndUtc, inspection.TechnicianName, inspection.VehicleRegistrationNumber));
        Assert.Single(demo, item => item.AssignmentId is null);

        // The morning's field work happened at its planned times, so the report has real numbers to show.
        var morning = Assert.Single(demo, item => item.FieldWorkStatus == "Completed");
        var completed = Assert.Single(await EventsAsync(client, "visit", morning.VisitId), e => e.EventType == "VisitWorkCompleted").Payload;
        Assert.Equal((25m, 85m, 15m, 70m),
            (completed.GetProperty("travelDurationMinutes").GetDecimal(), completed.GetProperty("grossWorkMinutes").GetDecimal(),
                completed.GetProperty("pauseMinutes").GetDecimal(), completed.GetProperty("netWorkMinutes").GetDecimal()));

        var incident = Assert.Single(board.Incidents, entry => entry.Incident.Title == DemoSeeder.IncidentTitle).Incident;
        Assert.Equal(("New", "Critical", Local(12, 45), Local(14, 15)), (incident.Status, incident.Priority, incident.RequestedStart, incident.RequestedEnd));
        var team = TeamOf(board);
        Assert.Equal(["Available", "Working", "Available"], team.Select(member => member.State));
        Assert.Equal("+48601100201", team[0].PhoneNumber);
        Assert.Contains(board.Alerts, alert => alert.Kind == "IncidentAwaitingDecision" && alert.EntityId == incident.Id);

        // Seeding again changes nothing.
        var again = await PostAsync<DemoSeedResult>(client, "/api/demo/seed", null);
        Assert.Equal((false, incident.Id, DateOnly.FromDateTime(Now.DateTime)), (again.CreatedDayPlan, again.IncidentId, again.Day));
        Assert.Equal(5, Demo(await BoardAsync(client)).Count);

        // 2. Analyze: both electricians are busy, so the incident needs a reschedule.
        var candidates = team.Select(member => member.TechnicianId).ToArray();
        await AnalyzeAsync(client, incident.Id, candidates);
        board = await BoardAsync(client);
        var analysed = Assert.Single(board.Incidents, entry => entry.Incident.Id == incident.Id);
        Assert.Equal(("ReadyForDispatch", 0, 2), (analysed.Incident.Status, analysed.Analysis!.DirectAssignmentCount, analysed.Analysis.RequiresRescheduleCount));
        Assert.Contains(board.Alerts, alert => alert.Kind == "IncidentNeedsReschedule" && alert.EntityId == incident.Id);

        // 3. The proposal: Jan, moving his inspection after the incident (+30 min travel); Anna's work is already under way.
        var before = JsonSerializer.Serialize(Demo(board));
        var proposals = await PostAsync<RescheduleProposal[]>(client, $"/api/incidents/{incident.Id}/reschedule-proposal",
            new RescheduleProposalRequest(candidates, null, null, null, null));
        var best = proposals[0];
        Assert.Equal(("Jan Kowalski", true, (Guid?)inspection.VisitId, "Normal"), (best.TechnicianName, best.IsFeasible, best.ConflictingVisitId, best.ConflictingPriority));
        Assert.Equal((Local(14, 45), Local(16, 15), 150, 1), (best.ProposedNewStartUtc!.Value, best.ProposedNewEndUtc!.Value, best.Impact.DelayMinutes, best.Impact.AffectedCustomerCount));
        Assert.Contains(proposals, proposal => proposal.TechnicianName == "Anna Nowak" && !proposal.IsFeasible);
        Assert.Equal(before, JsonSerializer.Serialize(Demo(await BoardAsync(client)))); // a proposal changes nothing

        // 4. The manager accepts; accepting the same proposal again is refused.
        using var accepted = await ApplyAsync(client, incident.Id, best);
        using var twice = await ApplyAsync(client, incident.Id, best);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (accepted.StatusCode, twice.StatusCode));
        Assert.Equal("Incident already dispatched", (await twice.Content.ReadFromJsonAsync<ProblemDetails>(Cancellation))!.Title);

        // 5. The board shows the new plan and why it changed; nothing about the incident is left to decide.
        board = await BoardAsync(client);
        demo = Demo(board);
        var moved = Assert.Single(demo, item => item.VisitId == inspection.VisitId);
        Assert.Equal((Local(14, 45), Local(16, 15), inspection.AssignmentId), (moved.VisitStartUtc, moved.VisitEndUtc, moved.AssignmentId));
        Assert.Equal(("UrgentIncident", (Guid?)incident.Id, Local(12, 15), 150),
            (moved.LastReschedule!.Reason, moved.LastReschedule.IncidentId, moved.LastReschedule.OldStartUtc, moved.LastReschedule.DelayMinutes));
        var urgent = Assert.Single(board.Today, item => item.SiteName == "Szpital — budynek B");
        Assert.Equal((Local(12, 45), "Jan Kowalski", "Urgent"), (urgent.VisitStartUtc, urgent.TechnicianName, urgent.WorkOrderPriority));
        Assert.Equal("Dispatched", Assert.Single(board.Incidents, entry => entry.Incident.Id == incident.Id).Incident.Status);
        Assert.DoesNotContain(board.Alerts, alert => alert.EntityId == incident.Id);
        Assert.Contains(board.Alerts, alert => alert.Kind == "VisitRescheduled" && alert.EntityId == inspection.VisitId && alert.Minutes == 150);

        // 6. The calendar agrees with the board (Jan's day), and the history keeps the decision.
        var calendar = await GetAsync<OperationalCalendarResponse>(client,
            $"/api/operational-calendar?start={Uri.EscapeDataString(board.Today.Min(i => i.VisitStartUtc).AddHours(-1).ToString("O"))}"
            + $"&end={Uri.EscapeDataString(Local(23, 0).ToString("O"))}&timeZoneId=Europe/Warsaw&perspective=technician&perspectiveId={team[0].TechnicianId}");
        Assert.Equal(
            board.Today.Where(item => item.TechnicianId == team[0].TechnicianId).Select(item => (item.VisitId, item.VisitStartUtc, item.AssignmentId)),
            calendar.Items.Select(item => (item.VisitId, item.VisitStartUtc, item.AssignmentId)));
        var history = await EventsAsync(client, "visit", inspection.VisitId);
        var decision = Assert.Single(history, e => e.EventType == "VisitRescheduled");
        Assert.Equal(("UrgentIncident", incident.Id), (decision.Payload.GetProperty("reason").GetString(), decision.Payload.GetProperty("incidentId").GetGuid()));
    }

    private WebApplicationFactory<Program> DemoHost() =>
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("Demo:Seed", "true")
            .ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedClock(Now))));

    private static Task<OperationsBoardResponse> BoardAsync(HttpClient client) =>
        GetAsync<OperationsBoardResponse>(client, "/api/operations/board?date=2041-03-12");

    /// <summary>The demo company's visits (other tests' data shares the database).</summary>
    private static List<OperationalCalendarItemResponse> Demo(OperationsBoardResponse board) =>
        [.. board.Today.Where(item => item.CustomerName == "ABC Instalacje Sp. z o.o.")];

    private static List<OperationsBoardTechnicianResponse> TeamOf(OperationsBoardResponse board) =>
        [.. DemoSeeder.Team.Select(member => Assert.Single(board.Team, t => t.DisplayName == member.Name && t.PhoneNumber == member.Phone.Replace(" ", "")))];

    private static DateTimeOffset Local(int hour, int minute) => new DateTimeOffset(2041, 3, 12, hour, minute, 0, TimeSpan.FromHours(1)).ToUniversalTime();

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid incidentId, RescheduleProposal proposal) =>
        client.PostAsJsonAsync($"/api/incidents/{incidentId}/reschedule-proposal/apply",
            new ApplyRescheduleRequest(proposal.RecommendedTechnicianId, proposal.VehicleId, proposal.EquipmentIds,
                proposal.TravelBufferBeforeMinutes, proposal.TravelBufferAfterMinutes,
                proposal.ConflictingVisitId, proposal.ProposedNewStartUtc, proposal.ProposedNewEndUtc),
            Cancellation);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
