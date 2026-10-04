namespace CrewCall.Scheduling.Core

open System

/// The kinds of resource a visit can occupy. Each technician, vehicle and equipment asset is a separate resource.
type ResourceKind =
    | Technician
    | Vehicle
    | Equipment

/// Identifies one resource.
type ResourceKey = { Kind: ResourceKind; ResourceId: Guid }

/// An existing occupation of one resource during [Range.Start, Range.End), optionally belonging to a visit.
type ResourceBooking =
    { BookingId: Guid
      Resource: ResourceKey
      Range: TimeRange
      VisitId: Guid option }

/// Time kept free before and after a visit (travel, setup). Both are non-negative.
type TravelBuffer = { Before: TimeSpan; After: TimeSpan }

/// Why a travel buffer cannot be applied.
type TravelBufferError =
    | NegativeBuffer
    /// The buffered range would fall outside the representable DateTimeOffset range.
    | OutOfRange

/// A clash between a requested visit and an existing booking of the same resource.
type ResourceConflict =
    /// The booking overlaps the visit itself.
    | BookingOverlap of ResourceBooking
    /// The booking overlaps only the travel buffer around the visit, not the visit.
    | TravelBufferOverlap of ResourceBooking

[<RequireQualifiedAccess>]
module TravelBuffer =

    let none = { Before = TimeSpan.Zero; After = TimeSpan.Zero }

    /// The range a visit occupies its resources for: [start - before, end + after), in UTC.
    /// Fails, without throwing, for a negative buffer or a result outside the DateTimeOffset range.
    let effectiveRange (buffer: TravelBuffer) (visit: TimeRange) : Result<TimeRange, TravelBufferError> =
        if buffer.Before < TimeSpan.Zero || buffer.After < TimeSpan.Zero then
            Error NegativeBuffer
        else
            let start = visit.Start.ToUniversalTime()
            let finish = visit.End.ToUniversalTime()

            if start.UtcTicks - DateTimeOffset.MinValue.UtcTicks < buffer.Before.Ticks
               || DateTimeOffset.MaxValue.UtcTicks - finish.UtcTicks < buffer.After.Ticks then
                Error OutOfRange
            else
                match TimeRange.create (start - buffer.Before) (finish + buffer.After) with
                | Ok range -> Ok range
                | Error _ -> Error OutOfRange

[<RequireQualifiedAccess>]
module ResourceConflicts =

    /// The bookings of `resources` that clash with a visit: those overlapping `effectiveRange` (the visit plus its
    /// travel buffer), classified as BookingOverlap when they overlap `visit` itself, otherwise TravelBufferOverlap.
    /// Touching ranges do not clash. Bookings of `ignoredVisit` are skipped, so a visit never conflicts with itself.
    /// Deterministic: sorted by resource, then booking start, end and id, whatever the input order.
    let detect
        (resources: ResourceKey seq)
        (visit: TimeRange)
        (effectiveRange: TimeRange)
        (ignoredVisit: Guid option)
        (bookings: ResourceBooking seq)
        : ResourceConflict list =
        let wanted = Set.ofSeq resources

        let isIgnored (booking: ResourceBooking) =
            match ignoredVisit, booking.VisitId with
            | Some ignored, Some visitId -> ignored = visitId
            | _ -> false

        bookings
        |> Seq.filter (fun booking -> wanted.Contains booking.Resource && not (isIgnored booking))
        |> Seq.filter (fun booking -> TimeRange.overlaps booking.Range effectiveRange)
        |> Seq.sortBy (fun booking -> booking.Resource, booking.Range.Start, booking.Range.End, booking.BookingId)
        |> Seq.map (fun booking ->
            if TimeRange.overlaps booking.Range visit then
                BookingOverlap booking
            else
                TravelBufferOverlap booking)
        |> Seq.toList
