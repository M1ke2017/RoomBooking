namespace CrewCall.Scheduling.Core

open System

/// What an operator could do with a candidate for urgent work (ADR-0012). Advisory: a suggestion never changes the plan.
type SuggestionKind =
    /// Feasible and free: can be dispatched as is, without touching existing work.
    | DirectAssignment
    /// Feasible (active, skilled, available), but existing bookings collide: only an operator who changes the existing
    /// plan first can dispatch this candidate. The system never moves that work by itself.
    | RequiresReschedule
    /// Inactive, missing required skills or unavailable: no change of the plan makes the candidate fit.
    | Unavailable

type DispatchSuggestion =
    { CandidateId: Guid
      Kind: SuggestionKind
      /// DirectAssignment: the matching score. RequiresReschedule: the score the candidate would have once its conflicts
      /// were resolved (its workload still counts the conflicting work). Unavailable: none.
      Score: CandidateScore option
      /// Empty for DirectAssignment; the colliding bookings for RequiresReschedule; every reason for Unavailable.
      Reasons: MatchRejectionReason list }

[<RequireQualifiedAccess>]
module DispatchSuggestions =

    /// Classifies one candidate. Uses the matching inputs and scoring unchanged: DirectAssignment exactly when
    /// Matching.evaluate finds the candidate Eligible, with the same score.
    let classify (policy: ScoringPolicy) (candidate: MatchingCandidate) : DispatchSuggestion =
        let conflicts = candidate.Conflicts |> List.map Conflict

        match candidate.Feasibility with
        | FeasibilityResult.Rejected reasons ->
            { CandidateId = candidate.CandidateId
              Kind = SuggestionKind.Unavailable
              Score = None
              Reasons = (reasons |> List.map Infeasible) @ conflicts }
        | Feasible ->
            { CandidateId = candidate.CandidateId
              Kind = (if List.isEmpty conflicts then DirectAssignment else RequiresReschedule)
              Score = Some(Matching.scoreEligible policy candidate)
              Reasons = conflicts }

    /// Classifies and orders every candidate: DirectAssignment first, in matching rank order; then RequiresReschedule
    /// in the same order (score, workload, id); then Unavailable by id. Deterministic: independent of the input order.
    let suggest (policy: ScoringPolicy) (candidates: MatchingCandidate seq) : DispatchSuggestion list =
        let classified =
            candidates
            |> Seq.map (fun candidate -> candidate, classify policy candidate)
            |> Seq.toList

        let scored kind =
            classified
            |> List.filter (fun (_, suggestion) -> suggestion.Kind = kind)
            |> List.sortWith (fun (a, suggestionA) (b, suggestionB) ->
                Matching.compareScored
                    (Option.get suggestionA.Score, a.Workload, a.CandidateId)
                    (Option.get suggestionB.Score, b.Workload, b.CandidateId))
            |> List.map snd

        let unavailable =
            classified
            |> List.map snd
            |> List.filter (fun suggestion -> suggestion.Kind = SuggestionKind.Unavailable)
            |> List.sortBy (fun suggestion -> suggestion.CandidateId)

        scored DirectAssignment @ scored RequiresReschedule @ unavailable
