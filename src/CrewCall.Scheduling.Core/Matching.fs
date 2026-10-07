namespace CrewCall.Scheduling.Core

open System

/// How much each signal counts. Normalized: the components are scaled so that they always add up to at most 100,
/// whatever the weights sum to; each weight must be non-negative and at least one positive.
type ScoringWeights =
    { Skills: decimal
      Availability: decimal
      Workload: decimal
      TeamPreference: decimal }

/// The scoring configuration: weights plus the workload at which the workload component reaches zero.
type ScoringPolicy =
    { Weights: ScoringWeights
      /// Assigned minutes in the workload window at which WorkloadScore reaches 0 (linear from 0 minutes).
      FullWorkloadMinutes: int }

/// How the candidate's skills relate to the requirement (normalized codes).
type SkillCoverage =
    { RequiredCount: int
      MatchedCount: int
      /// Skills beyond the requirement. Reported for explanation; they do not raise the score.
      AdditionalCount: int }

/// The candidate's current load in the evaluation window, computed by the caller from active assignments.
type CandidateWorkload =
    { AssignedMinutesInWindow: int
      AssignmentCountInWindow: int }

/// Whether the request named a preferred team and the candidate belongs to it. A preference, never a requirement.
type PreferredTeamMatch =
    | NoPreference
    | InPreferredTeam
    | NotInPreferredTeam

/// Everything the ranking needs about one candidate; prepared by the caller, no I/O here.
type MatchingCandidate =
    { CandidateId: Guid
      /// The 6A feasibility decision (active, skills, availability).
      Feasibility: FeasibilityResult
      /// Clashes of the requested resources with existing bookings: the candidate's own, and the requested vehicle
      /// and equipment (those reject every candidate alike, they never change a technician's score).
      Conflicts: ResourceConflict list
      SkillCoverage: SkillCoverage
      Workload: CandidateWorkload
      PreferredTeamMatch: PreferredTeamMatch }

/// Each component in points; they add up to TotalScore. Each is at most its normalized weight.
type ScoreComponents =
    { SkillScore: decimal
      AvailabilityScore: decimal
      WorkloadScore: decimal
      TeamPreferenceScore: decimal }

/// An explainable score on a 0–100 scale: TotalScore is exactly the sum of the components.
type CandidateScore =
    { TotalScore: decimal
      Components: ScoreComponents }

/// Why a candidate is not eligible.
type MatchRejectionReason =
    | Infeasible of RejectionReason
    | Conflict of ResourceConflict

/// Feasibility is the gate: a rejected candidate gets reasons, never a score.
type MatchResult =
    | Eligible of CandidateScore
    | Rejected of MatchRejectionReason list

type RankedCandidate =
    { CandidateId: Guid
      Workload: CandidateWorkload
      Result: MatchResult }

[<RequireQualifiedAccess>]
module ScoringPolicy =

    /// Skills 35, availability 30, workload 25, team preference 10; workload score reaches 0 at a full 8-hour day.
    let defaults =
        { Weights =
            { Skills = 35m
              Availability = 30m
              Workload = 25m
              TeamPreference = 10m }
          FullWorkloadMinutes = 480 }

    /// Checks the policy; the error says what is wrong.
    let validate (policy: ScoringPolicy) : Result<ScoringPolicy, string> =
        let w = policy.Weights

        if w.Skills < 0m || w.Availability < 0m || w.Workload < 0m || w.TeamPreference < 0m then
            Error "Weights must not be negative."
        elif w.Skills + w.Availability + w.Workload + w.TeamPreference <= 0m then
            Error "At least one weight must be positive."
        elif policy.FullWorkloadMinutes <= 0 then
            Error "FullWorkloadMinutes must be positive."
        else
            Ok policy

[<RequireQualifiedAccess>]
module Matching =

    /// How many required skills the candidate has, and how many more it has (normalized codes; blank codes ignored).
    let skillCoverage (candidateSkills: Set<string>) (requiredSkills: Set<string>) : SkillCoverage =
        let candidate = Skills.normalize candidateSkills
        let required = Skills.normalize requiredSkills

        { RequiredCount = required.Count
          MatchedCount = Set.intersect candidate required |> Set.count
          AdditionalCount = Set.difference candidate required |> Set.count }

    // Two decimals, always rounded down, so rounded components can never add up to more than 100.
    let private points (value: decimal) = Math.Floor(value * 100m) / 100m

    let private clamp01 (value: decimal) = max 0m (min 1m value)

    /// The score of a candidate that passed the gate. Each component is its normalized weight times a 0–1 factor:
    /// skills = share of required skills held (1 for an eligible candidate; extra skills add nothing);
    /// availability = 1 (eligibility already requires full availability; there is no partial availability);
    /// workload = 1 - assigned minutes / FullWorkloadMinutes, floored at 0 (less work scores higher);
    /// team preference = 1 in the preferred team or with no preference (neutral: everyone equal), 0 outside it.
    let scoreEligible (policy: ScoringPolicy) (candidate: MatchingCandidate) : CandidateScore =
        let w = policy.Weights
        let total = w.Skills + w.Availability + w.Workload + w.TeamPreference
        let scaled weight factor = points (weight * 100m / total * clamp01 factor)

        let skillFactor =
            if candidate.SkillCoverage.RequiredCount = 0 then
                1m
            else
                decimal candidate.SkillCoverage.MatchedCount / decimal candidate.SkillCoverage.RequiredCount

        let workloadFactor =
            1m - decimal (max 0 candidate.Workload.AssignedMinutesInWindow) / decimal policy.FullWorkloadMinutes

        let teamFactor =
            match candidate.PreferredTeamMatch with
            | NoPreference
            | InPreferredTeam -> 1m
            | NotInPreferredTeam -> 0m

        let components =
            { SkillScore = scaled w.Skills skillFactor
              AvailabilityScore = scaled w.Availability 1m
              WorkloadScore = scaled w.Workload workloadFactor
              TeamPreferenceScore = scaled w.TeamPreference teamFactor }

        { TotalScore =
            components.SkillScore
            + components.AvailabilityScore
            + components.WorkloadScore
            + components.TeamPreferenceScore
          Components = components }

    /// Feasibility as a gate: any infeasibility or resource conflict rejects the candidate with every reason;
    /// otherwise it is scored.
    let evaluate (policy: ScoringPolicy) (candidate: MatchingCandidate) : MatchResult =
        let infeasible =
            match candidate.Feasibility with
            | Feasible -> []
            | FeasibilityResult.Rejected reasons -> reasons |> List.map Infeasible

        match infeasible @ (candidate.Conflicts |> List.map Conflict) with
        | [] -> Eligible(scoreEligible policy candidate)
        | reasons -> Rejected reasons

    /// The order of scored candidates: TotalScore descending, then fewer assigned minutes, then fewer assignments,
    /// then candidate id. A total order, so sorting with it is deterministic.
    let compareScored
        (scoreA: CandidateScore, workloadA: CandidateWorkload, idA: Guid)
        (scoreB: CandidateScore, workloadB: CandidateWorkload, idB: Guid)
        : int =
        match compare scoreB.TotalScore scoreA.TotalScore with
        | 0 ->
            match compare workloadA.AssignedMinutesInWindow workloadB.AssignedMinutesInWindow with
            | 0 ->
                match compare workloadA.AssignmentCountInWindow workloadB.AssignmentCountInWindow with
                | 0 -> compare idA idB
                | byCount -> byCount
            | byMinutes -> byMinutes
        | byScore -> byScore

    /// Scores every candidate and orders them: eligible before rejected; eligible by compareScored;
    /// rejected by CandidateId. Deterministic: the result does not depend on the input order.
    let rank (policy: ScoringPolicy) (candidates: MatchingCandidate seq) : RankedCandidate list =
        let ranked =
            candidates
            |> Seq.map (fun candidate ->
                { CandidateId = candidate.CandidateId
                  Workload = candidate.Workload
                  Result = evaluate policy candidate })
            |> Seq.toList

        let eligible =
            ranked
            |> List.choose (fun candidate ->
                match candidate.Result with
                | Eligible score -> Some(candidate, score)
                | Rejected _ -> None)
            |> List.sortWith (fun (a, scoreA) (b, scoreB) ->
                compareScored (scoreA, a.Workload, a.CandidateId) (scoreB, b.Workload, b.CandidateId))
            |> List.map fst

        let rejected =
            ranked
            |> List.filter (fun candidate ->
                match candidate.Result with
                | Rejected _ -> true
                | Eligible _ -> false)
            |> List.sortBy (fun candidate -> candidate.CandidateId)

        eligible @ rejected
