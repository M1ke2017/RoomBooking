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
    | Rejected reasons -> not expectedFeasible && not (List.isEmpty reasons)

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
