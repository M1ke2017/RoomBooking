using CrewCall.Persistence;
using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Matching;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Scheduling.Tests;

public sealed class ResourceMatchingServiceTests(SchedulingDatabase database)
{
    private static int _daySequence;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A UTC day no other matching test uses, so workload windows never see other tests' assignments.</summary>
    private static DateTimeOffset NewDay() =>
        new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _daySequence));

    private static ResourceMatching Request(
        DateTimeOffset day,
        Guid[]? candidates = null,
        string[]? skills = null,
        Guid? preferredTeam = null,
        int? maxResults = null,
        Guid? vehicleId = null,
        Guid[]? equipment = null) =>
        new(day.AddHours(10), day.AddHours(11), skills, preferredTeam, candidates, maxResults, vehicleId, equipment, null, null, null);

    private async Task<ResourceMatchingOutcome> MatchAsync(ResourceMatching request)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ResourceMatchingService>().MatchAsync(request, Cancellation);
    }

    private async Task<ResourceMatchingResult> MatchedAsync(ResourceMatching request) =>
        Assert.IsType<ResourceMatchingOutcome.Matched>(await MatchAsync(request)).Result;

    /// <summary>An assignment for the technician over [start, end), with its reservation (a real claim through AssignmentService).</summary>
    private async Task AssignAsync(Guid technicianId, DateTimeOffset start, DateTimeOffset end, int after = 0)
    {
        var visitId = database.Visits.Add(start, end);
        await using var scope = database.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AssignmentService>()
            .CreateAsync(new CreateAssignment(visitId, technicianId, null, null, null, null, after), Cancellation);
        Assert.IsType<AssignOutcome.Assigned>(outcome);
    }

    [Fact]
    public async Task Without_a_candidate_filter_all_active_technicians_are_discovered()
    {
        var technicians = new FakeTechnicianSource();
        var first = technicians.AddTechnician("Ada");
        var second = technicians.AddTechnician("Bob");
        technicians.AddTechnician("Inactive", isActive: false);
        await using var provider = database.CreateIsolatedProvider(technicians);
        await using var scope = provider.CreateAsyncScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<ResourceMatchingService>().MatchAsync(Request(NewDay()), Cancellation);

        var result = Assert.IsType<ResourceMatchingOutcome.Matched>(outcome).Result;
        Assert.Equal(2, result.CandidatesEvaluated);
        Assert.False(result.CandidatesTruncated);
        Assert.Equal(new[] { first, second }.Order(), result.EligibleCandidates.Select(c => c.TechnicianId).Order());
        Assert.Empty(result.RejectedCandidates);
    }

    [Fact]
    public async Task Discovery_is_capped_and_reports_truncation()
    {
        var technicians = new FakeTechnicianSource();
        for (var index = 0; index < ResourceMatchingService.MaxCandidates + 3; index++)
        {
            technicians.AddTechnician($"Tech {index}");
        }

        await using var provider = database.CreateIsolatedProvider(technicians);
        await using var scope = provider.CreateAsyncScope();

        var result = Assert.IsType<ResourceMatchingOutcome.Matched>(
            await scope.ServiceProvider.GetRequiredService<ResourceMatchingService>().MatchAsync(Request(NewDay(), maxResults: 50), Cancellation)).Result;

        Assert.Equal(ResourceMatchingService.MaxCandidates, result.CandidatesEvaluated);
        Assert.True(result.CandidatesTruncated);
        Assert.Equal(ResourceMatchingService.MaxCandidates, result.TotalEligible);
        Assert.Equal(50, result.EligibleCandidates.Count);
    }

    [Fact]
    public async Task A_candidate_filter_ranks_only_those_technicians_and_an_inactive_one_is_rejected()
    {
        var day = NewDay();
        var chosen = database.Technicians.AddTechnician("Chosen");
        var inactive = database.Technicians.AddTechnician("Inactive", isActive: false, availability: TechnicianAvailabilityState.Inactive);
        database.Technicians.AddTechnician("Not requested");

        var result = await MatchedAsync(Request(day, [chosen, inactive]));

        Assert.Equal(2, result.CandidatesEvaluated);
        Assert.Equal(chosen, Assert.Single(result.EligibleCandidates).TechnicianId);
        var rejected = Assert.Single(result.RejectedCandidates);
        Assert.Equal((inactive, "Inactive"), (rejected.TechnicianId, rejected.TechnicianName));
        Assert.Equal([SchedulingConflictCode.TechnicianInactive], rejected.Reasons.Select(r => r.Code));
    }

    [Fact]
    public async Task Skills_are_mapped_and_a_missing_skill_rejects()
    {
        var day = NewDay();
        var skilled = database.Technicians.AddTechnician("Skilled", skillCodes: ["ELECTRICAL", "HVAC", "FIBER"]);
        var unskilled = database.Technicians.AddTechnician("Unskilled", skillCodes: ["ELECTRICAL"]);

        var result = await MatchedAsync(Request(day, [skilled, unskilled], skills: ["electrical", "hvac"]));

        var eligible = Assert.Single(result.EligibleCandidates);
        Assert.Equal(skilled, eligible.TechnicianId);
        Assert.Equal(new MatchSkillCoverage(2, 2, 1), eligible.SkillCoverage);
        Assert.Equal(35m, eligible.Components.SkillScore);
        var rejected = Assert.Single(result.RejectedCandidates);
        Assert.Equal(SchedulingConflictCode.MissingRequiredSkills, Assert.Single(rejected.Reasons).Code);
        Assert.Equal(["HVAC"], rejected.Reasons[0].Details);
    }

    [Fact]
    public async Task Workload_counts_active_assignments_in_the_day_and_lowers_the_score()
    {
        var day = NewDay();
        var idle = database.Technicians.AddTechnician("Idle");
        var busy = database.Technicians.AddTechnician("Busy");
        await AssignAsync(busy, day.AddHours(6), day.AddHours(8));                 // 120 min
        await AssignAsync(busy, day.AddHours(13), day.AddHours(14), after: 30);    // 60 min + 30 min buffer
        await AssignAsync(busy, day.AddDays(1).AddHours(6), day.AddDays(1).AddHours(8)); // next day: outside the window

        // A reservation made outside any assignment is not workload.
        await using (var scope = database.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResourceReservationService>().CreateAsync(
                new CreateReservation("Technician", idle, null, day.AddHours(1), day.AddHours(2)), Cancellation);
        }

        var result = await MatchedAsync(Request(day, [idle, busy]));

        Assert.Equal([idle, busy], result.EligibleCandidates.Select(c => c.TechnicianId));
        var busyCandidate = result.EligibleCandidates[1];
        Assert.Equal(new MatchWorkload(210, 2), busyCandidate.Workload);
        Assert.Equal(new MatchWorkload(0, 0), result.EligibleCandidates[0].Workload);
        Assert.Equal(14.06m, busyCandidate.Components.WorkloadScore); // 25 * (480 - 210) / 480 = 14.0625, floored to 2 decimals
        Assert.True(result.EligibleCandidates[0].TotalScore > busyCandidate.TotalScore);
        Assert.Equal((day, day.AddDays(1)), (result.WorkloadWindowStart, result.WorkloadWindowEnd));
    }

    [Fact]
    public async Task Team_preference_is_mapped_into_the_score()
    {
        var day = NewDay();
        var team = database.Technicians.AddTeam();
        var member = database.Technicians.AddTechnician("Member", teamId: team);
        var outsider = database.Technicians.AddTechnician("Outsider");

        var preferred = await MatchedAsync(Request(day, [member, outsider], preferredTeam: team));
        var neutral = await MatchedAsync(Request(day, [member, outsider]));

        Assert.Equal([member, outsider], preferred.EligibleCandidates.Select(c => c.TechnicianId));
        Assert.Equal((true, 10m), (preferred.EligibleCandidates[0].InPreferredTeam!.Value, preferred.EligibleCandidates[0].Components.TeamPreferenceScore));
        Assert.Equal((false, 0m), (preferred.EligibleCandidates[1].InPreferredTeam!.Value, preferred.EligibleCandidates[1].Components.TeamPreferenceScore));
        Assert.All(neutral.EligibleCandidates, c => Assert.Equal((null, 100m), (c.InPreferredTeam, c.TotalScore)));
    }

    [Fact]
    public async Task Unavailable_and_already_booked_technicians_are_rejected()
    {
        var day = NewDay();
        var absent = database.Technicians.AddTechnician("Absent", availability: TechnicianAvailabilityState.Absence);
        var booked = database.Technicians.AddTechnician("Booked");
        await AssignAsync(booked, day.AddHours(10).AddMinutes(30), day.AddHours(12));

        var result = await MatchedAsync(Request(day, [absent, booked]));

        Assert.Empty(result.EligibleCandidates);
        Assert.Equal(
            new[] { (absent, SchedulingConflictCode.TechnicianUnavailable), (booked, SchedulingConflictCode.TechnicianReservationConflict) }.OrderBy(x => x.Item1),
            result.RejectedCandidates.Select(c => (c.TechnicianId, Assert.Single(c.Reasons).Code)));
    }

    [Fact]
    public async Task A_busy_vehicle_rejects_every_candidate_without_touching_scores()
    {
        var day = NewDay();
        var first = database.Technicians.AddTechnician("First");
        var second = database.Technicians.AddTechnician("Second");
        var vehicleId = database.Resources.AddVehicle();
        var freeVehicle = database.Resources.AddVehicle();
        await using (var scope = database.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResourceReservationService>().CreateAsync(
                new CreateReservation("Vehicle", vehicleId, Guid.NewGuid(), day.AddHours(10), day.AddHours(11)), Cancellation);
        }

        var busy = await MatchedAsync(Request(day, [first, second], vehicleId: vehicleId));
        var free = await MatchedAsync(Request(day, [first, second], vehicleId: freeVehicle));

        Assert.Empty(busy.EligibleCandidates);
        Assert.All(busy.RejectedCandidates, c => Assert.Equal(SchedulingConflictCode.VehicleReservationConflict, Assert.Single(c.Reasons).Code));
        Assert.All(free.EligibleCandidates, c => Assert.Equal(100m, c.TotalScore));
    }

    [Fact]
    public async Task MaxResults_limits_eligible_candidates_and_totals_stay_complete()
    {
        var day = NewDay();
        var ids = Enumerable.Range(0, 5).Select(index => database.Technicians.AddTechnician($"T{index}")).ToArray();

        var result = await MatchedAsync(Request(day, ids, maxResults: 2));

        Assert.Equal(2, result.EligibleCandidates.Count);
        Assert.Equal(5, result.TotalEligible);
        Assert.Equal([1, 2], result.EligibleCandidates.Select(c => c.Rank));
        Assert.Equal(ids.Order().Take(2), result.EligibleCandidates.Select(c => c.TechnicianId)); // equal scores: by id
    }

    [Fact]
    public async Task Missing_entities_and_invalid_requests_are_reported()
    {
        var day = NewDay();
        var technician = database.Technicians.AddTechnician("Known");

        var missing = Assert.IsType<ResourceMatchingOutcome.NotFound>(
            await MatchAsync(Request(day, [technician, Guid.NewGuid()], preferredTeam: Guid.NewGuid(), vehicleId: Guid.NewGuid())));
        Assert.Equal(["Technician", "Team", "Vehicle"], missing.Missing.Select(m => m.Kind));

        var reversed = Assert.IsType<ResourceMatchingOutcome.Invalid>(
            await MatchAsync(Request(day) with { Start = day.AddHours(11), End = day.AddHours(10) }));
        Assert.Equal(["end"], reversed.Errors.Keys);

        var badLimits = Assert.IsType<ResourceMatchingOutcome.Invalid>(await MatchAsync(Request(day, [], maxResults: 51)));
        Assert.Equal(["candidateTechnicianIds", "maxResults"], badLimits.Errors.Keys.Order());
    }

    [Fact]
    public async Task Matching_writes_nothing()
    {
        var day = NewDay();
        var technician = database.Technicians.AddTechnician("Not claimed");

        var result = await MatchedAsync(Request(day, [technician]));

        // The top candidate is only advice: no reservation, assignment or event is created for it.
        Assert.Equal(technician, Assert.Single(result.EligibleCandidates).TechnicianId);
        await using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrewCallDbContext>();
        Assert.False(await db.ResourceReservations.AnyAsync(r => r.ResourceId == technician, Cancellation));
        Assert.False(await db.Assignments.AnyAsync(a => a.TechnicianId == technician, Cancellation));
    }
}
