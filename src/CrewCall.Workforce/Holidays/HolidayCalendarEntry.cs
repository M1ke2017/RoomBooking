namespace CrewCall.Workforce.Holidays;

/// <summary>
/// A public holiday: a whole calendar day, interpreted in each technician's own time zone. A national holiday has no
/// region; a regional one (e.g. a holiday in one state) carries the subdivision code.
/// </summary>
/// <remarks>
/// Unique per (CountryCode, RegionCode, Date), with "no region" counting as a value: one national entry per country
/// and day, plus at most one entry per region and day. Technicians have no region yet, so availability applies national
/// holidays only; regional entries are stored for when they do.
/// </remarks>
public sealed class HolidayCalendarEntry
{
    public const int NameMaxLength = 200;
    public const int RegionCodeMaxLength = 3;

    internal HolidayCalendarEntry(Guid id, DateOnly date, string name, string countryCode, string? regionCode)
    {
        Id = id;
        Date = date;
        Name = name;
        CountryCode = countryCode;
        RegionCode = regionCode;
    }

    public Guid Id { get; private set; }

    public DateOnly Date { get; private set; }

    public string Name { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string CountryCode { get; private set; }

    /// <summary>The ISO 3166-2 subdivision part after the country (e.g. "BY" for DE-BY), upper case; null for national.</summary>
    public string? RegionCode { get; private set; }
}
