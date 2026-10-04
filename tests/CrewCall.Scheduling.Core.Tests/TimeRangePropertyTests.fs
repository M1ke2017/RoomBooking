module CrewCall.Scheduling.Core.Tests.TimeRangePropertyTests

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open CrewCall.Scheduling.Core

/// Generates only valid ranges, built through TimeRange.create.
/// Starts and lengths sit on a quarter-hour grid within one day, so generated pairs regularly
/// overlap, touch exactly at a boundary and stay apart. Offsets vary to exercise instant-based comparison.
type ValidTimeRanges =
    static member TimeRange() : Arbitrary<TimeRange> =
        let dayStart = DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)

        gen {
            let! startSlot = Gen.choose (0, 95)
            let! lengthSlots = Gen.choose (1, 24)
            let! offsetHours = Gen.choose (-12, 14)

            let start = dayStart.AddMinutes(float (startSlot * 15)).ToOffset(TimeSpan.FromHours(float offsetHours))

            match TimeRange.create start (start.AddMinutes(float (lengthSlots * 15))) with
            | Ok range -> return range
            | Error error -> return failwithf "Generator produced an invalid range: %A" error
        }
        |> Arb.fromGen

[<Property(Arbitrary = [| typeof<ValidTimeRanges> |], MaxTest = 500)>]
let ``overlaps is symmetric`` (a: TimeRange) (b: TimeRange) =
    TimeRange.overlaps a b = TimeRange.overlaps b a
