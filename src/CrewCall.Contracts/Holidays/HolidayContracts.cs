namespace CrewCall.Contracts.Holidays;

/// <param name="Date">Calendar day, "yyyy-MM-dd".</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, e.g. "PL".</param>
/// <param name="RegionCode">Optional ISO 3166-2 subdivision part, e.g. "BY"; omit for a national holiday.</param>
public sealed record CreateHolidayRequest(DateOnly? Date, string? Name, string? CountryCode, string? RegionCode);

public sealed record HolidayResponse(Guid Id, DateOnly Date, string Name, string CountryCode, string? RegionCode);
