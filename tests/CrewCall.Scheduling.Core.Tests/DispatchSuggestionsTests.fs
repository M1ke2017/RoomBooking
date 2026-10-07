module CrewCall.Scheduling.Core.Tests.DispatchSuggestionsTests

open System
open Xunit
open FsCheck.Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges
open CrewCall.Scheduling.Core.Tests.SchedulingPropertyTests

let private policy = ScoringPolicy.defaults

let private id (n: int) = Guid(n, 0s, 0s, Array.zeroCreate 8)

let private candidate n : MatchingCandidate =
    { CandidateId = id n
      Feasibility = Feasible
      Conflicts = []
      SkillCoverage = { RequiredCount = 1; MatchedCount = 1; AdditionalCount = 0 }
      Workload = { AssignedMinutesInWindow = 0; AssignmentCountInWindow = 0 }
      PreferredTeamMatch = NoPreference }

let private booking n : ResourceBooking =
    { BookingId = id (1000 + n)
      Resource = { Kind = Technician; ResourceId = id n }
      Range = hours 10 11
      VisitId = Some(id (2000 + n)) }

let private busy n minutes : MatchingCandidate =
    { candidate n with
        Conflicts = [ BookingOverlap(booking n) ]
        Workload = { AssignedMinutesInWindow = minutes; AssignmentCountInWindow = 1 } }

[<Fact>]
let ``a feasible candidate without conflicts is a direct assignment with its matching score`` () =
    let suggestion = DispatchSuggestions.classify policy (candidate 1)

    Assert.Equal(DirectAssignment, suggestion.Kind)
    Assert.Empty(suggestion.Reasons)

    match Matching.evaluate policy (candidate 1) with
    | MatchResult.Eligible score -> Assert.Equal(Some score, suggestion.Score)
    | MatchResult.Rejected reasons -> failwithf "Expected eligible, got %A" reasons

[<Fact>]
let ``a feasible but booked candidate requires a reschedule, keeps a score and names the conflict`` () =
    let suggestion = DispatchSuggestions.classify policy (busy 1 60)

    Assert.Equal(RequiresReschedule, suggestion.Kind)
    Assert.True(suggestion.Score.IsSome)
    Assert.Equal<MatchRejectionReason list>([ Conflict(BookingOverlap(booking 1)) ], suggestion.Reasons)

[<Fact>]
let ``a travel buffer clash also requires a reschedule`` () =
    let buffered = { candidate 1 with Conflicts = [ TravelBufferOverlap(booking 1) ] }

    Assert.Equal(RequiresReschedule, (DispatchSuggestions.classify policy buffered).Kind)

[<Theory>]
[<InlineData("inactive")>]
[<InlineData("skills")>]
[<InlineData("absence")>]
let ``an infeasible candidate is unavailable without a score, even when it is also booked`` (why: string) =
    let reason =
        match why with
        | "inactive" -> CandidateInactive
        | "skills" -> MissingRequiredSkills [ "FIBER" ]
        | _ -> OutsideAvailability [ Absence ]

    let suggestion =
        DispatchSuggestions.classify policy { busy 1 60 with Feasibility = FeasibilityResult.Rejected [ reason ] }

    Assert.Equal(SuggestionKind.Unavailable, suggestion.Kind)
    Assert.Equal(None, suggestion.Score)
    Assert.Equal<MatchRejectionReason list>([ Infeasible reason; Conflict(BookingOverlap(booking 1)) ], suggestion.Reasons)

[<Fact>]
let ``suggestions list direct assignments in rank order, then reschedules by score, then unavailable by id`` () =
    let candidates =
        [ { candidate 9 with Feasibility = FeasibilityResult.Rejected [ CandidateInactive ] }
          busy 5 240
          { candidate 3 with Workload = { AssignedMinutesInWindow = 120; AssignmentCountInWindow = 1 } }
          { candidate 8 with Feasibility = FeasibilityResult.Rejected [ MissingRequiredSkills [ "X" ] ] }
          busy 4 60
          candidate 7 ]

    let suggestions = DispatchSuggestions.suggest policy candidates

    Assert.Equal<Guid list>([ id 7; id 3; id 4; id 5; id 8; id 9 ], suggestions |> List.map (fun s -> s.CandidateId))
    Assert.Equal<SuggestionKind list>(
        [ DirectAssignment; DirectAssignment; RequiresReschedule; RequiresReschedule; SuggestionKind.Unavailable; SuggestionKind.Unavailable ],
        suggestions |> List.map (fun s -> s.Kind))

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``direct assignments are exactly the eligible candidates, in the matching rank order`` (candidates: MatchingCandidate list) =
    let distinct = candidates |> List.distinctBy (fun c -> c.CandidateId)

    let direct =
        DispatchSuggestions.suggest policy distinct
        |> List.filter (fun s -> s.Kind = DirectAssignment)
        |> List.map (fun s -> s.CandidateId)

    let eligible =
        Matching.rank policy distinct
        |> List.filter (fun r ->
            match r.Result with
            | MatchResult.Eligible _ -> true
            | MatchResult.Rejected _ -> false)
        |> List.map (fun r -> r.CandidateId)

    direct = eligible

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``suggestions do not depend on input order and never drop a candidate`` (candidates: MatchingCandidate list) =
    let distinct =
        candidates
        |> List.distinctBy (fun c -> c.CandidateId)
        |> List.mapi (fun i c -> if i % 3 = 0 then { c with Conflicts = [ BookingOverlap(booking i) ] } else c)

    let suggestions = DispatchSuggestions.suggest policy distinct

    suggestions = DispatchSuggestions.suggest policy (List.rev distinct)
    && List.length suggestions = List.length distinct
