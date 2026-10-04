namespace CrewCall.Scheduling.Reservations;

/// <param name="ResourceType">Technician, Vehicle or Equipment.</param>
public sealed record CreateReservation(string? ResourceType, Guid ResourceId, Guid? VisitId, DateTimeOffset? Start, DateTimeOffset? End);

public abstract record CreateReservationOutcome
{
    private CreateReservationOutcome()
    {
    }

    public sealed record Created(ResourceReservation Reservation) : CreateReservationOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateReservationOutcome;

    public sealed record ResourceNotFound(ResourceType ResourceType, Guid ResourceId) : CreateReservationOutcome;

    /// <summary>The resource is already reserved for an overlapping period.</summary>
    public sealed record Overlaps(Guid ExistingReservationId) : CreateReservationOutcome;
}

/// <summary>A reservation that clashes with a requested visit.</summary>
/// <param name="WithinTravelBuffer">True when it overlaps only the travel buffer around the visit, not the visit.</param>
public sealed record ReservationConflict(ResourceReservation Reservation, bool WithinTravelBuffer);
