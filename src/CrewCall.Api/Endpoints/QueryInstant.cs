using System.Globalization;

namespace CrewCall.Api.Endpoints;

/// <summary>
/// Parses instants passed in query strings: ISO 8601 with an explicit offset or "Z". A time without an offset is
/// rejected, because it would otherwise be read in the server's time zone.
/// </summary>
internal static class QueryInstant
{
    private static readonly string[] _offsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    private static readonly string[] _utcFormats =
    [
        "yyyy-MM-dd'T'HH:mm'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    /// <summary>Missing values return null without an error (the caller decides whether they are required).</summary>
    public static DateTimeOffset? Parse(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // An unencoded "+" in a query string arrives as a space ("...T08:00:00 02:00"). A valid timestamp never contains
        // a space, so restoring the "+" is unambiguous.
        var text = value.Trim().Replace(' ', '+');

        if (DateTimeOffset.TryParseExact(text, _offsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return withOffset;
        }

        if (DateTimeOffset.TryParseExact(text, _utcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
        {
            return utc;
        }

        errors[field] = ["Must be an ISO 8601 date and time with an offset, e.g. 2026-03-30T08:00:00+02:00 or 2026-03-30T06:00:00Z."];
        return null;
    }
}
