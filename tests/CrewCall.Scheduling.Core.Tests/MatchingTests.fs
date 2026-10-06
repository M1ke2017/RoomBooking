module CrewCall.Scheduling.Core.Tests.MatchingTests

open System
open Xunit
open CrewCall.Scheduling.Core
open CrewCall.Scheduling.Core.Tests.TestRanges

let private policy = ScoringPolicy.defaults

let private id (n: int) = Guid(n, 0s, 0s, Array.zeroCreate 8)

let private candidate n : MatchingCandidate =
    { CandidateId = id n
      Feasibility = Feasible
      Conflicts = []
      SkillCoverage = { RequiredCount = 2; MatchedCount = 2; AdditionalCount = 0 }
      Workload = { AssignedMinutesInWindow = 0; AssignmentCountInWindow = 0 }
      PreferredTeamMatch = NoPreference }

let private withMinutes minutes (c: MatchingCandidate) : MatchingCandidate =
    { c with Workload = { AssignedMinutesInWindow = minutes; AssignmentCountInWindow = (if minutes > 0 then 1 else 0) } }

let private score (c: MatchingCandidate) =
    match Matching.evaluate policy c with
    | MatchResult.Eligible score -> score
    | MatchResult.Rejected reasons -> failwithf "Expected eligible, got %A" reasons

let private reasons (c: MatchingCandidate) =
    match Matching.evaluate policy c with
    | MatchResult.Rejected reasons -> reasons
    | MatchResult.Eligible score -> failwithf "Expected rejected, got %A" score

let private sumOf (s: CandidateScore) =
    s.Components.SkillScore + s.Components.AvailabilityScore + s.Components.WorkloadScore + s.Components.TeamPreferenceScore

[<Fact>]
let ``a feasible idle candidate gets the full score with explicit components`` () =
    let s = score (candidate 1)

    Assert.Equal(100m, s.TotalScore)
    Assert.Equal(
        { SkillScore = 35m; AvailabilityScore = 30m; WorkloadScore = 25m; TeamPreferenceScore = 10m },
        s.Components)

[<Fact>]
let ``a rejected candidate gets reasons and never a score`` () =
    let inactive = { candidate 1 with Feasibility = FeasibilityResult.Rejected [ CandidateInactive ] }

    Assert.Equal<MatchRejectionReason list>([ Infeasible CandidateInactive ], reasons inactive)

[<Fact>]
let ``a missing required skill rejects the candidate`` () =
    let unskilled =
        { candidate 1 with
            Feasibility = FeasibilityResult.Rejected [ MissingRequiredSkills [ "FIBER" ] ]
            SkillCoverage = { RequiredCount = 2; MatchedCount = 1; AdditionalCount = 0 } }

    Assert.Equal<MatchRejectionReason list>([ Infeasible(MissingRequiredSkills [ "FIBER" ]) ], reasons unskilled)

[<Fact>]
let ``an unavailable candidate is rejected`` () =
    let absent = { candidate 1 with Feasibility = FeasibilityResult.Rejected [ OutsideAvailability [ Absence ] ] }

    Assert.Equal<MatchRejectionReason list>([ Infeasible(OutsideAvailability [ Absence ]) ], reasons absent)

[<Fact>]
let ``a booking conflict rejects the candidate alongside infeasibility reasons`` () =
    let booking =
        { BookingId = id 99
          Resource = { Kind = Technician; ResourceId = id 1 }
          Range = hours 10 11
          VisitId = None }

    let busy : MatchingCandidate =
        { candidate 1 with
            Feasibility = FeasibilityResult.Rejected [ CandidateInactive ]
            Conflicts = [ BookingOverlap booking ] }

    Assert.Equal<MatchRejectionReason list>([ Infeasible CandidateInactive; Conflict(BookingOverlap booking) ], reasons busy)

[<Fact>]
let ``lower workload scores higher and a full day scores zero`` () =
    let idle = score (candidate 1)
    let half = score (candidate 2 |> withMinutes 240)
    let full = score (candidate 3 |> withMinutes 480)
    let over = score (candidate 4 |> withMinutes 900)

    Assert.Equal(25m, idle.Components.WorkloadScore)
    Assert.Equal(12.5m, half.Components.WorkloadScore)
    Assert.Equal(0m, full.Components.WorkloadScore)
    Assert.Equal(0m, over.Components.WorkloadScore)
    Assert.True(idle.TotalScore > half.TotalScore && half.TotalScore > full.TotalScore)

[<Fact>]
let ``the preferred team gives a bonus and no preference is neutral`` () =
    let inTeam = score { candidate 1 with PreferredTeamMatch = InPreferredTeam }
    let outside = score { candidate 2 with PreferredTeamMatch = NotInPreferredTeam }
    let neutral = score { candidate 3 with PreferredTeamMatch = NoPreference }

    Assert.Equal(10m, inTeam.Components.TeamPreferenceScore)
    Assert.Equal(0m, outside.Components.TeamPreferenceScore)
    Assert.Equal(inTeam.TotalScore, neutral.TotalScore)
    Assert.Equal(inTeam.TotalScore - 10m, outside.TotalScore)

[<Fact>]
let ``additional skills do not raise the skill score`` () =
    let specialist = score { candidate 1 with SkillCoverage = { RequiredCount = 2; MatchedCount = 2; AdditionalCount = 9 } }
    let noRequirement = score { candidate 2 with SkillCoverage = { RequiredCount = 0; MatchedCount = 0; AdditionalCount = 3 } }

    Assert.Equal(35m, specialist.Components.SkillScore)
    Assert.Equal(35m, noRequirement.Components.SkillScore)

[<Fact>]
let ``components always add up to the total, within 0 to 100, also with uneven weights`` () =
    let uneven =
        { Weights = { Skills = 1m; Availability = 1m; Workload = 1m; TeamPreference = 0m }
          FullWorkloadMinutes = 7 }

    for minutes in [ 0; 1; 3; 7; 50 ] do
        match Matching.evaluate uneven (candidate 1 |> withMinutes minutes) with
        | MatchResult.Eligible s ->
            Assert.Equal(sumOf s, s.TotalScore)
            Assert.InRange(s.TotalScore, 0m, 100m)
        | MatchResult.Rejected r -> failwithf "Unexpected %A" r

[<Fact>]
let ``skill coverage counts required, matched and additional skills with normalized codes`` () =
    let coverage = Matching.skillCoverage (set [ "electrical"; " HVAC"; "Fiber" ]) (set [ "ELECTRICAL"; "hvac"; "Welding" ])

    Assert.Equal({ RequiredCount = 3; MatchedCount = 2; AdditionalCount = 1 }, coverage)

[<Fact>]
let ``the policy rejects negative weights, all-zero weights and a non-positive workload horizon`` () =
    let w = ScoringPolicy.defaults.Weights
    let isError = function Error _ -> true | Ok _ -> false

    Assert.True(isError (ScoringPolicy.validate { ScoringPolicy.defaults with Weights = { w with Workload = -1m } }))
    Assert.True(isError (ScoringPolicy.validate { ScoringPolicy.defaults with Weights = { Skills = 0m; Availability = 0m; Workload = 0m; TeamPreference = 0m } }))
    Assert.True(isError (ScoringPolicy.validate { ScoringPolicy.defaults with FullWorkloadMinutes = 0 }))
    Assert.Equal(Ok ScoringPolicy.defaults, ScoringPolicy.validate ScoringPolicy.defaults)

[<Fact>]
let ``ranking puts eligible first by score then workload then id, rejected last`` () =
    let rejected : MatchingCandidate = { candidate 1 with Feasibility = FeasibilityResult.Rejected [ CandidateInactive ] }
    let busy = candidate 2 |> withMinutes 240
    let idleOutsideTeam : MatchingCandidate = { candidate 3 with PreferredTeamMatch = NotInPreferredTeam }
    let idle = candidate 4

    let order = Matching.rank policy [ rejected; busy; idleOutsideTeam; idle ] |> List.map (fun r -> r.CandidateId)

    // idle 100, idleOutsideTeam 90, busy 87.5, then the rejected one.
    Assert.Equal<Guid list>([ id 4; id 3; id 2; id 1 ], order)

[<Fact>]
let ``equal scores are ordered by fewer assigned minutes, then fewer assignments, then candidate id`` () =
    // A tiny workload that does not change the rounded score still decides the tie.
    let fewMinutes : MatchingCandidate = { candidate 9 with Workload = { AssignedMinutesInWindow = 0; AssignmentCountInWindow = 0 } }
    let moreMinutes : MatchingCandidate = { candidate 1 with Workload = { AssignedMinutesInWindow = 0; AssignmentCountInWindow = 2 } }
    let tieA = candidate 5
    let tieB = candidate 3

    let order = Matching.rank policy [ moreMinutes; tieA; fewMinutes; tieB ] |> List.map (fun r -> r.CandidateId)

    Assert.Equal<Guid list>([ id 3; id 5; id 9; id 1 ], order)

[<Fact>]
let ``ranking is the same whatever the input order`` () =
    let candidates : MatchingCandidate list =
        [ candidate 1 |> withMinutes 100
          { candidate 2 with Feasibility = FeasibilityResult.Rejected [ CandidateInactive ] }
          candidate 3
          { candidate 4 with PreferredTeamMatch = NotInPreferredTeam }
          candidate 5 |> withMinutes 100 ]

    let expected = Matching.rank policy candidates

    Assert.Equal<RankedCandidate list>(expected, Matching.rank policy (List.rev candidates))
    Assert.Equal<RankedCandidate list>(expected, Matching.rank policy (List.sortBy (fun (c: MatchingCandidate) -> c.Workload.AssignedMinutesInWindow) candidates))
