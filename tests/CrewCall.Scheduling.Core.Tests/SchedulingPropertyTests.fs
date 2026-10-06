module CrewCall.Scheduling.Core.Tests.SchedulingPropertyTests

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open CrewCall.Scheduling.Core

/// Generators on a quarter-hour grid over two days, so generated ranges regularly overlap, touch and leave gaps.
/// Offsets vary so comparisons must be by instant.
type SchedulingArbitraries =
    static member private RangeGen: Gen<TimeRange> =
        let origin = DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)

        gen {
            let! startSlot = Gen.choose (0, 191)
            let! lengthSlots = Gen.choose (1, 32)
            let! offsetHours = Gen.choose (-12, 14)
            let start = origin.AddMinutes(float (startSlot * 15)).ToOffset(TimeSpan.FromHours(float offsetHours))

            match TimeRange.create start (start.AddMinutes(float (lengthSlots * 15))) with
            | Ok range -> return range
            | Error error -> return failwithf "Generator produced an invalid range: %A" error
        }

    static member TimeRange() : Arbitrary<TimeRange> = Arb.fromGen SchedulingArbitraries.RangeGen

    static member Candidate() : Arbitrary<SchedulingCandidate> =
        let skills = Gen.subListOf [ "ELECTRICAL"; "hvac"; "Fiber"; " diagnostics " ] |> Gen.map Set.ofList

        let window =
            Gen.oneof
                [ SchedulingArbitraries.RangeGen |> Gen.map Available
                  Gen.map2
                      (fun range reason -> Unavailable(range, reason))
                      SchedulingArbitraries.RangeGen
                      (Gen.elements [ Inactive; OutsideWorkingHours; Absence; Holiday; Other ]) ]

        gen {
            let! isActive = Gen.elements [ true; false ]
            let! windows = Gen.listOf window
            let! skillCodes = skills
            return
                { CandidateId = Guid.Parse "0199b2a0-0000-7000-8000-000000000002"
                  IsActive = isActive
                  Availability = windows
                  SkillCodes = skillCodes }
        }
        |> Arb.fromGen

    static member Requirement() : Arbitrary<SchedulingRequirement> =
        gen {
            let! range = SchedulingArbitraries.RangeGen
            let! skills = Gen.subListOf [ "electrical"; "HVAC"; "fiber" ]
            return { RequiredRange = range; RequiredSkillCodes = Set.ofList skills }
        }
        |> Arb.fromGen

let private instants (range: TimeRange) = range.Start.UtcTicks, range.End.UtcTicks

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``normalize is idempotent`` (ranges: TimeRange list) =
    let once = Ranges.normalizeRanges ranges
    List.map instants (Ranges.normalizeRanges once) = List.map instants once

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``normalized ranges are sorted and never overlap or touch`` (ranges: TimeRange list) =
    Ranges.normalizeRanges ranges
    |> List.pairwise
    |> List.forall (fun (earlier, later) -> earlier.End < later.Start)

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``normalize covers every input range and nothing outside them`` (ranges: TimeRange list) =
    let normalized = Ranges.normalizeRanges ranges

    let everyInputCovered =
        ranges |> List.forall (fun input -> normalized |> List.exists (fun period -> TimeRange.contains period input))

    // Each normalized period starts and ends on boundaries of input ranges, so no time is invented.
    let boundariesFromInput =
        normalized
        |> List.forall (fun period ->
            ranges |> List.exists (fun input -> input.Start = period.Start)
            && ranges |> List.exists (fun input -> input.End = period.End))

    everyInputCovered && boundariesFromInput

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``evaluateCandidate is deterministic and ignores window order``
    (candidate: SchedulingCandidate)
    (requirement: SchedulingRequirement)
    =
    let result = Feasibility.evaluateCandidate candidate requirement

    result = Feasibility.evaluateCandidate candidate requirement
    && result = Feasibility.evaluateCandidate { candidate with Availability = List.rev candidate.Availability } requirement

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``a candidate is feasible exactly when no check fails`` (candidate: SchedulingCandidate) (requirement: SchedulingRequirement) =
    let expectedFeasible =
        candidate.IsActive
        && Skills.hasRequiredSkills candidate.SkillCodes requirement.RequiredSkillCodes
        && Availability.covers requirement.RequiredRange candidate.Availability

    match Feasibility.evaluateCandidate candidate requirement with
    | Feasible -> expectedFeasible
    | FeasibilityResult.Rejected reasons -> not expectedFeasible && not (List.isEmpty reasons)

[<Property(Arbitrary = [| typeof<SchedulingArbitraries> |], MaxTest = 500)>]
let ``conflict detection does not depend on booking order and never reports touching bookings``
    (visit: TimeRange)
    (ranges: TimeRange list)
    =
    let resource = { Kind = Technician; ResourceId = Guid.Parse "0199b2a0-0000-7000-8000-000000000003" }

    let bookings =
        ranges
        |> List.mapi (fun index range ->
            { BookingId = Guid(index, 0s, 0s, Array.zeroCreate 8)
              Resource = resource
              Range = range
              VisitId = None })

    let detect bookings = ResourceConflicts.detect [ resource ] visit visit None bookings
    let conflicts = detect bookings

    conflicts = detect (List.rev bookings)
    && conflicts
       |> List.forall (function
           | BookingOverlap booking -> TimeRange.overlaps booking.Range visit
           | TravelBufferOverlap _ -> false)
    && List.length conflicts = (ranges |> List.filter (TimeRange.overlaps visit) |> List.length)

// ---------------------------------------------------------------- matching

type MatchingArbitraries =
    static member MatchingCandidate() : Arbitrary<MatchingCandidate> =
        gen {
            let! idSeed = Gen.choose (1, 50)
            let! feasible = Gen.frequency [ 4, Gen.constant true; 1, Gen.constant false ]
            let! required = Gen.choose (0, 4)
            let! extra = Gen.choose (0, 4)
            let! minutes = Gen.choose (0, 1000)
            let! count = Gen.choose (0, 6)
            let! team = Gen.elements [ NoPreference; InPreferredTeam; NotInPreferredTeam ]

            return
                { CandidateId = Guid(idSeed, 0s, 0s, Array.zeroCreate 8)
                  Feasibility = if feasible then Feasible else FeasibilityResult.Rejected [ CandidateInactive ]
                  Conflicts = []
                  SkillCoverage = { RequiredCount = required; MatchedCount = required; AdditionalCount = extra }
                  Workload = { AssignedMinutesInWindow = minutes; AssignmentCountInWindow = count }
                  PreferredTeamMatch = team }
        }
        |> Arb.fromGen

    static member ScoringPolicy() : Arbitrary<ScoringPolicy> =
        gen {
            let! weights = Gen.listOfLength 4 (Gen.choose (0, 60))
            let! horizon = Gen.choose (1, 1440)
            let weights = if List.sum weights = 0 then [ 1; 0; 0; 0 ] else weights

            return
                { Weights =
                    { Skills = decimal weights[0]
                      Availability = decimal weights[1]
                      Workload = decimal weights[2]
                      TeamPreference = decimal weights[3] }
                  FullWorkloadMinutes = horizon }
        }
        |> Arb.fromGen

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``a score is always within 0 to 100 and equals the sum of its components`` (policy: ScoringPolicy) (candidate: MatchingCandidate) =
    match Matching.evaluate policy candidate with
    | MatchResult.Eligible s ->
        let c = s.Components
        s.TotalScore >= 0m
        && s.TotalScore <= 100m
        && s.TotalScore = c.SkillScore + c.AvailabilityScore + c.WorkloadScore + c.TeamPreferenceScore
        && [ c.SkillScore; c.AvailabilityScore; c.WorkloadScore; c.TeamPreferenceScore ] |> List.forall (fun x -> x >= 0m)
    | MatchResult.Rejected reasons -> not (List.isEmpty reasons)

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``the same input always gives the same result`` (policy: ScoringPolicy) (candidates: MatchingCandidate list) =
    Matching.rank policy candidates = Matching.rank policy candidates

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``more workload never improves the workload score`` (policy: ScoringPolicy) (candidate: MatchingCandidate) (extraMinutes: uint16) =
    let feasible = { candidate with Feasibility = Feasible }

    let busier =
        { feasible with
            Workload = { feasible.Workload with AssignedMinutesInWindow = feasible.Workload.AssignedMinutesInWindow + int extraMinutes } }

    match Matching.evaluate policy feasible, Matching.evaluate policy busier with
    | MatchResult.Eligible less, Eligible more -> more.Components.WorkloadScore <= less.Components.WorkloadScore
    | _ -> false

[<Property(Arbitrary = [| typeof<MatchingArbitraries> |], MaxTest = 500)>]
let ``ranking does not depend on input order and keeps eligible before rejected`` (policy: ScoringPolicy) (candidates: MatchingCandidate list) =
    // Distinct ids: the id is the final tie-break, so two different candidates with one id could legitimately swap.
    let distinct = candidates |> List.distinctBy (fun c -> c.CandidateId)
    let ranked = Matching.rank policy distinct

    let isEligible r =
        match r.Result with
        | MatchResult.Eligible _ -> true
        | MatchResult.Rejected _ -> false

    ranked = Matching.rank policy (List.rev distinct)
    && (ranked |> List.map isEligible |> List.pairwise |> List.forall (fun (a, b) -> a || not b))
