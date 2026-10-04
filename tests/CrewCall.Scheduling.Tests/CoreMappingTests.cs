using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Ports;
using Xunit;

namespace CrewCall.Scheduling.Tests;

/// <summary>The adapter from Workforce's technician profile to the F# SchedulingCandidate, and the F# decision on it.</summary>
public sealed class CoreMappingTests
{
    private static readonly TimeRange _visit = CoreMapping.ToRange(
        new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero));

    private static TechnicianSchedulingProfile Profile(
        bool isActive = true, TechnicianAvailabilityState availability = TechnicianAvailabilityState.Available, params string[] skills) =>
        new(Guid.NewGuid(), isActive, skills, availability, null);

    [Fact]
    public void An_active_available_technician_maps_to_a_feasible_candidate()
    {
        var profile = Profile();

        var candidate = CoreMapping.ToCandidate(profile, _visit);

        Assert.Equal(profile.TechnicianId, candidate.CandidateId);
        Assert.True(candidate.IsActive);
        var window = Assert.Single(candidate.Availability);
        Assert.Equal(AvailabilityWindow.NewAvailable(_visit), window);
        Assert.Equal(FeasibilityResult.Feasible, Feasibility.evaluateCandidate(candidate, CoreMapping.ToRequirement(_visit, [])));
    }

    [Fact]
    public void Skills_map_to_the_candidates_skill_codes()
    {
        var candidate = CoreMapping.ToCandidate(Profile(skills: ["ELECTRICAL", "HVAC"]), _visit);

        Assert.Equal(["ELECTRICAL", "HVAC"], candidate.SkillCodes.OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal(
            FeasibilityResult.Feasible,
            Feasibility.evaluateCandidate(candidate, CoreMapping.ToRequirement(_visit, ["hvac", " Electrical "])));
    }

    [Theory]
    [InlineData(TechnicianAvailabilityState.Absence, "Absence")]
    [InlineData(TechnicianAvailabilityState.Holiday, "Holiday")]
    [InlineData(TechnicianAvailabilityState.OutsideWorkingHours, "OutsideWorkingHours")]
    [InlineData(TechnicianAvailabilityState.Inactive, "Inactive")]
    public void Workforce_unavailability_maps_to_an_unavailable_window_and_a_rejection(
        TechnicianAvailabilityState availability, string expectedReason)
    {
        var candidate = CoreMapping.ToCandidate(Profile(availability: availability), _visit);

        var window = Assert.IsType<AvailabilityWindow.Unavailable>(Assert.Single(candidate.Availability));
        Assert.Equal(_visit, window.Item1);
        Assert.Equal(expectedReason, window.Item2.ToString());

        var rejected = Assert.IsType<FeasibilityResult.Rejected>(
            Feasibility.evaluateCandidate(candidate, CoreMapping.ToRequirement(_visit, [])));
        var outside = Assert.IsType<RejectionReason.OutsideAvailability>(Assert.Single(rejected.Item));
        Assert.Equal(expectedReason, Assert.Single(outside.Item).ToString());
    }

    [Fact]
    public void Missing_skills_come_back_from_the_F_sharp_core()
    {
        var candidate = CoreMapping.ToCandidate(Profile(skills: ["ELECTRICAL"]), _visit);

        var rejected = Assert.IsType<FeasibilityResult.Rejected>(
            Feasibility.evaluateCandidate(candidate, CoreMapping.ToRequirement(_visit, ["electrical", "fiber", "welding"])));

        var missing = Assert.IsType<RejectionReason.MissingRequiredSkills>(Assert.Single(rejected.Item));
        Assert.Equal(["FIBER", "WELDING"], missing.Item);
    }
}
