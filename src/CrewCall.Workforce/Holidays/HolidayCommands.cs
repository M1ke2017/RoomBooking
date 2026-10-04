namespace CrewCall.Workforce.Holidays;

/// <param name="RegionCode">Optional ISO 3166-2 subdivision part (1–3 letters or digits, e.g. "BY"); null for a national holiday.</param>
public sealed record CreateHoliday(DateOnly? Date, string? Name, string? CountryCode, string? RegionCode);

public abstract record CreateHolidayOutcome
{
    private CreateHolidayOutcome()
    {
    }

    public sealed record Created(HolidayCalendarEntry Holiday) : CreateHolidayOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateHolidayOutcome;

    /// <summary>The country (and region) already has a holiday on that date.</summary>
    public sealed record AlreadyExists(Guid ExistingHolidayId) : CreateHolidayOutcome;
}

public abstract record ListHolidaysOutcome
{
    private ListHolidaysOutcome()
    {
    }

    public sealed record Listed(IReadOnlyList<HolidayCalendarEntry> Holidays) : ListHolidaysOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ListHolidaysOutcome;
}
