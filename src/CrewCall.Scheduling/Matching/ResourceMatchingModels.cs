using CrewCall.Scheduling.Checks;

namespace CrewCall.Scheduling.Matching;

/// <summary>
/// Which technicians could take a visit in [Start, End), and which fits best? Advisory and read-only: nothing is reserved
/// and no assignment is created (ADR-0011).
/// </summary>
/// <param name="PreferredTeamId">Optional: members of this team get a small bonus. A preference, never a requirement.</param>
/// <param name="CandidateTechnicianIds">Optional: rank only these. Without it, all active technicians (at most 200).</param>
/// <param name="MaxResults">Eligible candidates to return: default 10, at most 50.</param>
/// <param name="VehicleId">Optional: its conflicts make the request infeasible for every candidate; never part of a score.</param>
/// <param name="EquipmentIds">Optional, like the vehicle.</param>
/// <param name="VisitId">Optional: the visit being planned; its own reservations are ignored for conflicts and workload.</param>
/// <param name="WorkloadWindowStart">Optional, with WorkloadWindowEnd: where workload is counted. Default: the UTC days of the request.</param>
public sealed record ResourceMatching(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    IReadOnlyCollection<string>? RequiredSkillCodes,
    Guid? PreferredTeamId,
    IReadOnlyCollection<Guid>? CandidateTechnicianIds,
    int? MaxResults,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    Guid? VisitId,
    DateTimeOffset? WorkloadWindowStart,
    DateTimeOffset? WorkloadWindowEnd);

public sealed record MatchScoreComponents(decimal SkillScore, decimal AvailabilityScore, decimal WorkloadScore, decimal TeamPreferenceScore);

public sealed record MatchWorkload(int AssignedMinutes, int AssignmentCount);

public sealed record MatchSkillCoverage(int RequiredCount, int MatchedCount, int AdditionalCount);

/// <param name="Rank">1 for the best candidate.</param>
/// <param name="InPreferredTeam">Null when no preferred team was requested.</param>
public sealed record EligibleCandidate(
    int Rank,
    Guid TechnicianId,
    string TechnicianName,
    Guid? TeamId,
    decimal TotalScore,
    MatchScoreComponents Components,
    MatchWorkload Workload,
    MatchSkillCoverage SkillCoverage,
    bool? InPreferredTeam);

public sealed record RejectedCandidate(Guid TechnicianId, string TechnicianName, Guid? TeamId, IReadOnlyList<SchedulingConflict> Reasons);

/// <param name="CandidatesTruncated">True when more active technicians existed than the discovery limit.</param>
public sealed record ResourceMatchingResult(
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset WorkloadWindowStart,
    DateTimeOffset WorkloadWindowEnd,
    int CandidatesEvaluated,
    bool CandidatesTruncated,
    int TotalEligible,
    int TotalRejected,
    IReadOnlyList<EligibleCandidate> EligibleCandidates,
    IReadOnlyList<RejectedCandidate> RejectedCandidates);

public abstract record ResourceMatchingOutcome
{
    private ResourceMatchingOutcome()
    {
    }

    public sealed record Matched(ResourceMatchingResult Result) : ResourceMatchingOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ResourceMatchingOutcome;

    /// <summary>Requested technicians, team, vehicle or equipment that do not exist ("Technician", "Team", ...).</summary>
    public sealed record NotFound(IReadOnlyList<(string Kind, Guid Id)> Missing) : ResourceMatchingOutcome;
}
