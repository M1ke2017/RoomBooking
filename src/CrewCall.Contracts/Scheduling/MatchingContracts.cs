namespace CrewCall.Contracts.Scheduling;

/// <summary>
/// POST /api/scheduling/match: which technicians could take a visit in [Start, End), best first. Advisory: nothing is
/// reserved and no assignment is created; assignment runs its own final check.
/// </summary>
/// <param name="CandidateTechnicianIds">Optional subset to rank; default: all active technicians (at most 200).</param>
/// <param name="MaxResults">Eligible candidates returned: default 10, at most 50.</param>
/// <param name="VehicleId">Optional: a conflict makes every candidate infeasible; never part of a technician's score.</param>
/// <param name="VisitId">Optional: the visit being planned; its own reservations are ignored.</param>
/// <param name="WorkloadWindowStart">Optional, with WorkloadWindowEnd; default: the UTC days the request touches.</param>
public sealed record ResourceMatchingRequest(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string[]? RequiredSkillCodes,
    Guid? PreferredTeamId,
    Guid[]? CandidateTechnicianIds,
    int? MaxResults,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    Guid? VisitId,
    DateTimeOffset? WorkloadWindowStart,
    DateTimeOffset? WorkloadWindowEnd);

/// <param name="TotalEligible">All eligible candidates; EligibleCandidates holds at most MaxResults of them.</param>
/// <param name="TotalRejected">All rejected candidates; RejectedCandidates holds at most 50 of them.</param>
public sealed record ResourceMatchingResponse(
    DateTimeOffset RequestStart,
    DateTimeOffset RequestEnd,
    DateTimeOffset WorkloadWindowStart,
    DateTimeOffset WorkloadWindowEnd,
    int CandidatesEvaluated,
    bool CandidatesTruncated,
    int TotalEligible,
    int TotalRejected,
    EligibleCandidateResponse[] EligibleCandidates,
    RejectedCandidateResponse[] RejectedCandidates);

/// <param name="TotalScore">0–100, the sum of ScoreComponents.</param>
/// <param name="InPreferredTeam">Null when no preferred team was requested.</param>
public sealed record EligibleCandidateResponse(
    int Rank,
    Guid TechnicianId,
    string TechnicianName,
    Guid? TeamId,
    decimal TotalScore,
    ScoreComponentsResponse ScoreComponents,
    CandidateWorkloadResponse CurrentWorkload,
    SkillCoverageResponse SkillCoverage,
    bool? InPreferredTeam);

public sealed record ScoreComponentsResponse(decimal SkillScore, decimal AvailabilityScore, decimal WorkloadScore, decimal TeamPreferenceScore);

/// <param name="AssignedMinutes">Minutes of active assignments in the workload window (claimed windows, travel buffers included).</param>
public sealed record CandidateWorkloadResponse(int AssignedMinutes, int AssignmentCount);

public sealed record SkillCoverageResponse(int RequiredCount, int MatchedCount, int AdditionalCount);

/// <param name="Reasons">Same codes as the scheduling check (TechnicianInactive, TechnicianUnavailable, MissingRequiredSkills, ...ReservationConflict).</param>
public sealed record RejectedCandidateResponse(Guid TechnicianId, string TechnicianName, Guid? TeamId, SchedulingConflictResponse[] Reasons);
