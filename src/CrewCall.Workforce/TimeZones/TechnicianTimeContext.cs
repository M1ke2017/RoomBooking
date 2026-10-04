using NodaTime;
using NodaTime.TimeZones;

namespace CrewCall.Workforce.TimeZones;

/// <summary>
/// The IANA time zone database used by the Workforce module. NodaTime embeds a TZDB release in the package, so zone
/// rules are the same on every host (Linux, Windows, CI) and do not depend on the operating system's tzdata.
/// </summary>
public static class TechnicianTimeContext
{
    public const int TimeZoneIdMaxLength = 64;
    public const int CountryCodeLength = 2;

    // ISO 3166-1 alpha-2 codes as listed in the TZDB's iso3166.tab / zone.tab (every country appears there).
    private static readonly HashSet<string> _countryCodes = TzdbDateTimeZoneSource.Default.ZoneLocations?
        .Select(location => location.CountryCode)
        .ToHashSet(StringComparer.Ordinal) ?? [];

    /// <summary>The TZDB release the zone rules come from, e.g. "2025b".</summary>
    public static string TzdbVersion => TzdbDateTimeZoneSource.Default.TzdbVersion;

    /// <summary>
    /// Resolves an IANA zone id ("Europe/Warsaw"). Ids are case-sensitive; Windows ids ("Central European Standard
    /// Time") and fixed offsets ("+02:00") are not accepted.
    /// </summary>
    public static DateTimeZone? FindZone(string? timeZoneId) =>
        string.IsNullOrEmpty(timeZoneId) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId);

    /// <summary>Required zone: throws for a stored id the current TZDB no longer knows (it was validated on write).</summary>
    public static DateTimeZone GetZone(string timeZoneId) => DateTimeZoneProviders.Tzdb[timeZoneId];

    public static bool IsKnownCountryCode(string countryCode) => _countryCodes.Contains(countryCode);

    /// <summary>Validates and normalizes an ISO 3166-1 alpha-2 country code (stored in upper case).</summary>
    internal static string? CountryCode(this ValidationErrors errors, string field, string? value, bool required)
    {
        var text = required ? errors.Required(field, value, int.MaxValue) : errors.Optional(field, value, int.MaxValue);
        if (text is null)
        {
            return null;
        }

        var code = text.ToUpperInvariant();
        if (code.Length != CountryCodeLength || !code.All(char.IsAsciiLetterUpper) || !IsKnownCountryCode(code))
        {
            errors.Add(field, "Must be an ISO 3166-1 alpha-2 country code, e.g. PL.");
            return null;
        }

        return code;
    }

    internal static string? TimeZoneId(this ValidationErrors errors, string field, string? value)
    {
        var text = errors.Required(field, value, TimeZoneIdMaxLength);
        if (text is not null && text.Length <= TimeZoneIdMaxLength && FindZone(text) is null)
        {
            errors.Add(field, "Must be an IANA time zone id, e.g. Europe/Warsaw.");
            return null;
        }

        return text;
    }
}
