namespace CrewCall.Scheduling.Core

/// Rules over sets of time ranges. All functions are pure and leave their input unchanged.
[<RequireQualifiedAccess>]
module Ranges =

    /// Sorts the ranges by start (then end) and merges every pair that overlaps or touches, so the result is the
    /// smallest list of separate, continuous periods covering exactly the same time.
    /// Guarantees: sorted; no two result ranges overlap or touch; idempotent.
    /// The input may be in any order; it is not modified.
    let normalizeRanges (ranges: TimeRange seq) : TimeRange list =
        ranges
        |> Seq.sortBy (fun range -> range.Start, range.End)
        |> Seq.fold
            (fun merged range ->
                match merged with
                | last :: earlier ->
                    match TimeRange.merge last range with
                    | Some joined -> joined :: earlier
                    | None -> range :: merged
                | [] -> [ range ])
            []
        |> List.rev

    /// True when `required` lies entirely within one continuous period of `available`. Touching or overlapping ranges
    /// join into one period; a gap of any size breaks it. The ranges may be in any order.
    let isRangeFullyCovered (required: TimeRange) (available: TimeRange seq) : bool =
        normalizeRanges available |> List.exists (fun period -> TimeRange.contains period required)
