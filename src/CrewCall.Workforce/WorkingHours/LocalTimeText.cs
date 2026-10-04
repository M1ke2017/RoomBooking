using System.Globalization;

namespace CrewCall.Workforce.WorkingHours;

/// <summary>The "HH:mm" text form of working-hours times. Minute precision; "24:00" is the end of the day.</summary>
public static class LocalTimeText
{
    private const string Format = "HH':'mm";
    private const string EndOfDay = "24:00";

    public static string FormatStart(TimeOnly time) => time.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>An end of 00:00 is written as "24:00", so the range reads as running to midnight.</summary>
    public static string FormatEnd(TimeOnly time) => time == TimeOnly.MinValue ? EndOfDay : FormatStart(time);

    internal static TimeOnly? Parse(ValidationErrors errors, string field, string? value, bool isEnd)
    {
        var text = errors.Required(field, value, maxLength: 5);
        if (text is null)
        {
            return null;
        }

        if (isEnd && text == EndOfDay)
        {
            return TimeOnly.MinValue;
        }

        if (TimeOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        errors.Add(field, isEnd ? "Must be a time in HH:mm format (00:00–24:00)." : "Must be a time in HH:mm format (00:00–23:59).");
        return null;
    }
}
