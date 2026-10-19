using CrewCall.Contracts.OperationalCalendar;

namespace CrewCall.Persistence.ReadModels.OperationalCalendar;

/// <summary>Whose calendar: everything, or the visits of one technician, team, vehicle or site.</summary>
public enum OperationalCalendarPerspective
{
    All,
    Technician,
    Team,
    Vehicle,
    Site
}

/// <summary>
/// The visits overlapping the half-open range [Start, End), seen from one perspective, with local times in TimeZoneId.
/// The same query serves a day, a week or a month (at most <see cref="OperationalCalendarService.MaxRangeLength"/>).
/// </summary>
/// <param name="Perspective">All (default), Technician, Team, Vehicle or Site (case-insensitive).</param>
/// <param name="PerspectiveId">Required for every perspective except All, where it must be omitted.</param>
/// <param name="TimeZoneId">IANA id the local times are projected into, e.g. "Europe/Warsaw". Required.</param>
public sealed record OperationalCalendarQuery(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Perspective,
    Guid? PerspectiveId,
    string? TimeZoneId);

public abstract record OperationalCalendarOutcome
{
    private OperationalCalendarOutcome()
    {
    }

    /// <summary>The calendar, built directly as its API contract (ADR-0017 §11: no read-model copy in between).</summary>
    public sealed record Listed(OperationalCalendarResponse Calendar) : OperationalCalendarOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : OperationalCalendarOutcome;

    /// <summary>The technician, team, vehicle or site named by the perspective does not exist.</summary>
    public sealed record PerspectiveNotFound(OperationalCalendarPerspective Perspective, Guid PerspectiveId) : OperationalCalendarOutcome;
}
