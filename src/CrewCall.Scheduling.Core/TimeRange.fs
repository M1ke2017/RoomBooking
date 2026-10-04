namespace CrewCall.Scheduling.Core

open System

/// Why a time range could not be created.
type TimeRangeError =
    | EndMustBeAfterStart

/// A non-empty, half-open interval of time [Start, End).
/// The representation is private: the only way to obtain a TimeRange is TimeRange.create,
/// so every TimeRange satisfies Start < End.
type TimeRange =
    private
        { start: DateTimeOffset
          finish: DateTimeOffset }

    member this.Start = this.start
    member this.End = this.finish

[<RequireQualifiedAccess>]
module TimeRange =

    /// Creates a time range. Fails when the end is not strictly after the start.
    /// Comparison is by instant, so the two values may use different UTC offsets.
    let create (start: DateTimeOffset) (finish: DateTimeOffset) : Result<TimeRange, TimeRangeError> =
        if start < finish then
            Ok { start = start; finish = finish }
        else
            Error EndMustBeAfterStart

    /// True when the two ranges share a period of time.
    /// Ranges that only touch (one ends exactly when the other starts) do not overlap.
    let overlaps (a: TimeRange) (b: TimeRange) : bool =
        a.Start < b.End && a.End > b.Start

    /// True when `inner` lies entirely within `outer` (boundaries included): outer.Start <= inner.Start and
    /// inner.End <= outer.End.
    let contains (outer: TimeRange) (inner: TimeRange) : bool =
        outer.Start <= inner.Start && inner.End <= outer.End

    /// True when one range ends exactly when the other starts. Touching ranges do not overlap, but together they form
    /// one continuous period.
    let touches (a: TimeRange) (b: TimeRange) : bool =
        a.End = b.Start || b.End = a.Start

    /// The single range covering both, when they overlap or touch; None when there is a gap between them.
    /// The result keeps the UTC offset of whichever value it takes its start and end from.
    let merge (a: TimeRange) (b: TimeRange) : TimeRange option =
        if overlaps a b || touches a b then
            Some
                { start = (if a.Start <= b.Start then a.Start else b.Start)
                  finish = (if a.End >= b.End then a.End else b.End) }
        else
            None
