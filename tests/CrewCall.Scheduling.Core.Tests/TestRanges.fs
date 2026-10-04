/// Helpers shared by the scheduling-core tests.
module CrewCall.Scheduling.Core.Tests.TestRanges

open System
open CrewCall.Scheduling.Core

/// Monday 2026-10-05 in Warsaw summer time (UTC+2).
let private dayZero = DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours 2.0)

/// A point in time `day` days after 2026-10-05, at hour:minute local (UTC+2).
let at (day: int) (hour: int) (minute: int) =
    dayZero.AddDays(float day).AddHours(float hour).AddMinutes(float minute)

/// A range the test expects to be valid.
let range start finish =
    match TimeRange.create start finish with
    | Ok range -> range
    | Error error -> failwithf "Expected a valid range from %O to %O, got %A" start finish error

/// A range on day 0, from hour to hour (whole hours).
let hours (startHour: int) (endHour: int) = range (at 0 startHour 0) (at 0 endHour 0)

/// Start/end pairs, for readable equality assertions.
let bounds (ranges: TimeRange list) =
    ranges |> List.map (fun r -> r.Start, r.End)
