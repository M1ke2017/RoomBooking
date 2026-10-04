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
