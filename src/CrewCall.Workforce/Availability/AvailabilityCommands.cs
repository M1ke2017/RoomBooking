namespace CrewCall.Workforce.Availability;

/// <summary>Is the technician available for the whole half-open interval [Start, End)?</summary>
public sealed record CheckAvailability(Guid TechnicianId, DateTimeOffset? Start, DateTimeOffset? End);

/// <param name="LocalStart">Start as the technician's local time (with that moment's UTC offset).</param>
/// <param name="LocalEnd">End as the technician's local time (with that moment's UTC offset).</param>
public sealed record TechnicianAvailability(
    Guid TechnicianId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    DateTimeOffset LocalStart,
    DateTimeOffset LocalEnd,
    AvailabilityDecision Decision);

public abstract record CheckAvailabilityOutcome
{
    private CheckAvailabilityOutcome()
    {
    }

    public sealed record Evaluated(TechnicianAvailability Availability) : CheckAvailabilityOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CheckAvailabilityOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : CheckAvailabilityOutcome;
}
