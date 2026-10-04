namespace CrewCall.Scheduling.Core

/// Why a period is unavailable, as reported by whoever produced the availability (for example the Workforce module).
/// Scheduling.Core does not derive these; it only carries them into rejection reasons.
type AvailabilityReason =
    | Inactive
    | OutsideWorkingHours
    | Absence
    | Holiday
    | Other

/// One input window of a candidate's availability.
type AvailabilityWindow =
    | Available of TimeRange
    | Unavailable of TimeRange * AvailabilityReason

[<RequireQualifiedAccess>]
module Availability =

    /// The ranges marked available, in input order.
    let availableRanges (windows: AvailabilityWindow seq) : TimeRange list =
        windows
        |> Seq.choose (function
            | Available range -> Some range
            | Unavailable _ -> None)
        |> Seq.toList

    /// The reasons of the unavailable windows that overlap `required`: distinct, in declaration order of
    /// AvailabilityReason (so the result does not depend on input order).
    let unavailableReasonsWithin (required: TimeRange) (windows: AvailabilityWindow seq) : AvailabilityReason list =
        windows
        |> Seq.choose (function
            | Unavailable(range, reason) when TimeRange.overlaps range required -> Some reason
            | _ -> None)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toList

    /// True when `required` lies within one continuous available period and no unavailable window overlaps it.
    /// Unavailable windows win over available ones: an absence inside working hours still blocks.
    let covers (required: TimeRange) (windows: AvailabilityWindow seq) : bool =
        let windows = Seq.toList windows

        Ranges.isRangeFullyCovered required (availableRanges windows)
        && List.isEmpty (unavailableReasonsWithin required windows)
