namespace CrewCall.Contracts.Availability;

/// <summary>GET /api/technicians/{technicianId}/availability?start=...&amp;end=... for the half-open interval [Start, End).</summary>
/// <param name="Start">The requested start, UTC.</param>
/// <param name="End">The requested end, UTC.</param>
/// <param name="LocalStart">Start in the technician's time zone (with that moment's offset).</param>
/// <param name="LocalEnd">End in the technician's time zone (with that moment's offset).</param>
/// <param name="Reason">
/// Available, UnavailableInactiveTechnician, UnavailableHoliday, UnavailableAbsence or UnavailableOutsideWorkingHours.
/// </param>
/// <param name="Detail">What caused the unavailability (holiday date and name, absence type); null otherwise.</param>
public sealed record TechnicianAvailabilityResponse(
    Guid TechnicianId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    DateTimeOffset LocalStart,
    DateTimeOffset LocalEnd,
    bool IsAvailable,
    string Reason,
    string? Detail);
