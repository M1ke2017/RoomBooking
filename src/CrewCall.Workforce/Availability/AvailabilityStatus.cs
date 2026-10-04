namespace CrewCall.Workforce.Availability;

/// <summary>Why a technician is or is not available for an interval. Checked in this order; the first failing check wins.</summary>
public enum AvailabilityStatus
{
    Available,
    UnavailableInactiveTechnician,
    UnavailableHoliday,
    UnavailableAbsence,
    UnavailableOutsideWorkingHours
}

/// <param name="Detail">What caused the unavailability (e.g. the holiday name or absence type); null when available.</param>
public sealed record AvailabilityDecision(AvailabilityStatus Status, string? Detail)
{
    public bool IsAvailable => Status == AvailabilityStatus.Available;
}
