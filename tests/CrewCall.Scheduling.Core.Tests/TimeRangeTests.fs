module CrewCall.Scheduling.Core.Tests.TimeRangeTests

open System
open Xunit
open CrewCall.Scheduling.Core

let private warsawSummer = TimeSpan.FromHours 2.0

/// A point in time on 2026-10-05 (day 0) or a following day, at the given local time.
let private at (day: int) (hour: int) (minute: int) =
    DateTimeOffset(2026, 10, 5, hour, minute, 0, warsawSummer).AddDays(float day)

/// Creates a range that the test expects to be valid.
let private range start finish =
    match TimeRange.create start finish with
    | Ok range -> range
    | Error error -> failwithf "Expected a valid range from %O to %O, got %A" start finish error

// ---------------------------------------------------------------- creation

[<Fact>]
let ``create accepts a range whose end is after its start`` () =
    let start = at 0 10 0
    let finish = at 0 11 0

    match TimeRange.create start finish with
    | Ok range ->
        Assert.Equal(start, range.Start)
        Assert.Equal(finish, range.End)
    | Error error -> failwithf "Expected Ok, got %A" error

[<Fact>]
let ``create rejects a range whose end equals its start`` () =
    let instant = at 0 10 0

    Assert.Equal(Error EndMustBeAfterStart, TimeRange.create instant instant)

[<Fact>]
let ``create rejects a range whose end is before its start`` () =
    Assert.Equal(Error EndMustBeAfterStart, TimeRange.create (at 0 11 0) (at 0 10 0))

// ---------------------------------------------------------------- overlaps

[<Fact>]
let ``ranges that partially overlap conflict`` () =
    let a = range (at 0 10 0) (at 0 11 0)
    let b = range (at 0 10 30) (at 0 11 30)

    Assert.True(TimeRange.overlaps a b)

[<Fact>]
let ``partial overlap is detected when the later range is given first`` () =
    let a = range (at 0 10 0) (at 0 11 0)
    let b = range (at 0 10 30) (at 0 11 30)

    Assert.True(TimeRange.overlaps b a)

[<Fact>]
let ``a range conflicts with a range it contains`` () =
    let a = range (at 0 9 0) (at 0 12 0)
    let b = range (at 0 10 0) (at 0 11 0)

    Assert.True(TimeRange.overlaps a b)

[<Fact>]
let ``a range conflicts with a range that contains it`` () =
    let a = range (at 0 10 0) (at 0 11 0)
    let b = range (at 0 9 0) (at 0 12 0)

    Assert.True(TimeRange.overlaps a b)

[<Fact>]
let ``identical ranges conflict`` () =
    let a = range (at 0 10 0) (at 0 11 0)
    let b = range (at 0 10 0) (at 0 11 0)

    Assert.True(TimeRange.overlaps a b)

[<Fact>]
let ``ranges that only touch at a boundary do not conflict`` () =
    let a = range (at 0 10 0) (at 0 11 0)
    let b = range (at 0 11 0) (at 0 12 0)

    Assert.False(TimeRange.overlaps a b)
    Assert.False(TimeRange.overlaps b a)

[<Fact>]
let ``separate ranges do not conflict`` () =
    let a = range (at 0 8 0) (at 0 9 0)
    let b = range (at 0 13 0) (at 0 14 0)

    Assert.False(TimeRange.overlaps a b)

[<Fact>]
let ``ranges crossing midnight conflict when they share time`` () =
    let a = range (at 0 23 30) (at 1 0 30)
    let b = range (at 1 0 0) (at 1 1 0)

    Assert.True(TimeRange.overlaps a b)

[<Fact>]
let ``overlap is decided by instant, not by UTC offset`` () =
    // 09:00-10:00 UTC is 11:00-12:00 in Warsaw summer time, so it only touches 10:00-11:00 local.
    let local = range (at 0 10 0) (at 0 11 0)
    let utc = range (DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)) (DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero))

    Assert.False(TimeRange.overlaps local utc)
