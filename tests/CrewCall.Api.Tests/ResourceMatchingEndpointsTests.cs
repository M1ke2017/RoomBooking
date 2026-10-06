using System.Net;
using System.Net.Http.Json;
using CrewCall.Contracts.Absences;
using CrewCall.Contracts.Scheduling;
using CrewCall.Contracts.Skills;
using CrewCall.Contracts.Visits;
using CrewCall.Contracts.WorkingHours;
using Xunit;

namespace CrewCall.Api.Tests;

/// <summary>
/// Matching end to end on real Workforce data. Every test ranks only its own technicians (candidate subset) on its own
/// UTC day, so technicians and assignments of other tests in the shared database never take part.
/// </summary>
public sealed class ResourceMatchingEndpointsTests(CrewCallApiFactory factory)
{
    private static int _daySequence;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static DateTimeOffset NewDay() =>
        new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _daySequence));

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request, Cancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
    }

    private static async Task<Guid> CreateSkillAsync(HttpClient client, string code) =>
        (await PostAsync<SkillResponse>(client, "/api/skills", new CreateSkillRequest(code, code, null))).Id;

    /// <summary>A Warsaw technician working every day 00:00–24:00, with the given skills and team.</summary>
    private static async Task<Guid> CreateTechnicianAsync(HttpClient client, Guid? teamId = null, params Guid[] skillIds)
    {
        var technicianId = await ApiTestData.CreateTechnicianAsync(client);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await PostAsync<WorkingHoursResponse>(client, $"/api/technicians/{technicianId}/working-hours", new CreateWorkingHoursRequest(day, "00:00", "24:00"));
        }

        foreach (var skillId in skillIds)
        {
            (await client.PostAsync($"/api/technicians/{technicianId}/skills/{skillId}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        if (teamId is { } team)
        {
            (await client.PostAsync($"/api/teams/{team}/technicians/{technicianId}", null, Cancellation)).EnsureSuccessStatusCode();
        }

        return technicianId;
    }

    private static string UniqueCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    /// <summary>A real assignment (visit + claim) for the technician over [start, end).</summary>
    private static async Task AssignAsync(HttpClient client, Guid technicianId, DateTimeOffset start, DateTimeOffset end)
    {
        var workOrder = await ApiTestData.CreateWorkOrderAsync(client);
        var visit = await PostAsync<VisitResponse>(client, $"/api/work-orders/{workOrder.Id}/visits", new CreateVisitRequest(start, end, null));
        await PostAsync<AssignmentResponse>(client, $"/api/visits/{visit.Id}/assignment", new CreateAssignmentRequest(technicianId, null, null, null, null, null));
    }

    private static ResourceMatchingRequest Request(DateTimeOffset day, Guid[] candidates, string[]? skills = null, Guid? preferredTeam = null) =>
        new(day.AddHours(10), day.AddHours(11), skills, preferredTeam, candidates, null, null, null, null, null, null);

    private static async Task<ResourceMatchingResponse> MatchAsync(HttpClient client, ResourceMatchingRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/scheduling/match", request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResourceMatchingResponse>(Cancellation))!;
    }

    [Fact]
    public async Task Match_returns_200_with_the_best_candidate_first_and_an_explained_score()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var idle = await CreateTechnicianAsync(client);
        var busy = await CreateTechnicianAsync(client);
        await AssignAsync(client, busy, day.AddHours(6), day.AddHours(8)); // 120 minutes earlier that day

        var result = await MatchAsync(client, Request(day, [busy, idle]));

        Assert.Equal([idle, busy], result.EligibleCandidates.Select(c => c.TechnicianId));
        var best = result.EligibleCandidates[0];
        Assert.Equal((1, 100m), (best.Rank, best.TotalScore));
        Assert.Equal(new ScoreComponentsResponse(35m, 30m, 25m, 10m), best.ScoreComponents);
        Assert.Equal("Technician", best.TechnicianName);
        var second = result.EligibleCandidates[1];
        Assert.Equal(new CandidateWorkloadResponse(120, 1), second.CurrentWorkload);
        Assert.Equal(93.75m, second.TotalScore);
        Assert.Equal(
            second.TotalScore,
            second.ScoreComponents.SkillScore + second.ScoreComponents.AvailabilityScore + second.ScoreComponents.WorkloadScore + second.ScoreComponents.TeamPreferenceScore);
        Assert.Equal((day.AddHours(10), day, day.AddDays(1)), (result.RequestStart, result.WorkloadWindowStart, result.WorkloadWindowEnd));
    }

    [Fact]
    public async Task Missing_skills_and_absences_reject_candidates_with_reasons()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var code = UniqueCode("FIB");
        var skillId = await CreateSkillAsync(client, code);
        var skilled = await CreateTechnicianAsync(client, null, skillId);
        var unskilled = await CreateTechnicianAsync(client);
        var absent = await CreateTechnicianAsync(client, null, skillId);
        await PostAsync<AbsenceResponse>(client, $"/api/technicians/{absent}/absences",
            new CreateAbsenceRequest(day.AddHours(9), day.AddHours(12), "SickLeave", null));

        var result = await MatchAsync(client, Request(day, [skilled, unskilled, absent], [code.ToLowerInvariant()]));

        Assert.Equal(skilled, Assert.Single(result.EligibleCandidates).TechnicianId);
        var reasons = result.RejectedCandidates.ToDictionary(c => c.TechnicianId, c => Assert.Single(c.Reasons));
        Assert.Equal(("MissingRequiredSkills", code), (reasons[unskilled].Code, reasons[unskilled].Details.Single()));
        Assert.Equal("TechnicianUnavailable", reasons[absent].Code);
        Assert.Equal(2, result.TotalRejected);
    }

    [Fact]
    public async Task A_technician_booked_during_the_visit_is_rejected_and_one_booked_earlier_scores_lower()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var free = await CreateTechnicianAsync(client);
        var earlier = await CreateTechnicianAsync(client);
        var overlapping = await CreateTechnicianAsync(client);
        await AssignAsync(client, earlier, day.AddHours(5), day.AddHours(9));
        await AssignAsync(client, overlapping, day.AddHours(10).AddMinutes(30), day.AddHours(12));

        var result = await MatchAsync(client, Request(day, [free, earlier, overlapping]));

        Assert.Equal([free, earlier], result.EligibleCandidates.Select(c => c.TechnicianId));
        Assert.True(result.EligibleCandidates[0].TotalScore > result.EligibleCandidates[1].TotalScore);
        var rejected = Assert.Single(result.RejectedCandidates);
        Assert.Equal((overlapping, "TechnicianReservationConflict"), (rejected.TechnicianId, Assert.Single(rejected.Reasons).Code));
    }

    [Fact]
    public async Task The_preferred_team_raises_its_members_score()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var teamId = await ApiTestData.CreateTeamAsync(client);
        var member = await CreateTechnicianAsync(client, teamId);
        var outsider = await CreateTechnicianAsync(client);

        var preferred = await MatchAsync(client, Request(day, [outsider, member], preferredTeam: teamId));

        Assert.Equal([member, outsider], preferred.EligibleCandidates.Select(c => c.TechnicianId));
        Assert.Equal((true, 100m), (preferred.EligibleCandidates[0].InPreferredTeam!.Value, preferred.EligibleCandidates[0].TotalScore));
        Assert.Equal((false, 90m), (preferred.EligibleCandidates[1].InPreferredTeam!.Value, preferred.EligibleCandidates[1].TotalScore));
    }

    [Fact]
    public async Task Only_the_requested_candidates_are_ranked()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var first = await CreateTechnicianAsync(client);
        var second = await CreateTechnicianAsync(client);
        await CreateTechnicianAsync(client); // not requested

        var result = await MatchAsync(client, Request(day, [first, second]));

        Assert.Equal(2, result.CandidatesEvaluated);
        Assert.Equal(new[] { first, second }.Order(), result.EligibleCandidates.Select(c => c.TechnicianId).Order());
    }

    [Fact]
    public async Task Invalid_requests_return_400_and_missing_entities_404()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);

        using var reversed = await client.PostAsJsonAsync("/api/scheduling/match", Request(day, [technician]) with { Start = day.AddHours(12) }, Cancellation);
        using var tooLong = await client.PostAsJsonAsync("/api/scheduling/match", Request(day, [technician]) with { End = day.AddDays(40) }, Cancellation);
        using var maxResults = await client.PostAsJsonAsync("/api/scheduling/match", Request(day, [technician]) with { MaxResults = 0 }, Cancellation);
        using var missingTeam = await client.PostAsJsonAsync("/api/scheduling/match", Request(day, [technician], preferredTeam: Guid.NewGuid()), Cancellation);
        using var missingTechnician = await client.PostAsJsonAsync("/api/scheduling/match", Request(day, [technician, Guid.NewGuid()]), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, maxResults.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTeam.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTechnician.StatusCode);
    }

    [Fact]
    public async Task Matching_creates_no_assignment_for_the_top_candidate()
    {
        using var client = factory.CreateClient();
        var day = NewDay();
        var technician = await CreateTechnicianAsync(client);

        Assert.Equal(technician, Assert.Single((await MatchAsync(client, Request(day, [technician]))).EligibleCandidates).TechnicianId);

        // Still free for the very same slot: matching reserved nothing.
        var check = await PostAsync<SchedulingCheckResponse>(client, "/api/scheduling/check",
            new SchedulingCheckRequest(technician, null, null, null, day.AddHours(10), day.AddHours(11), null, null, null));
        Assert.True(check.IsFeasible);
    }
}
