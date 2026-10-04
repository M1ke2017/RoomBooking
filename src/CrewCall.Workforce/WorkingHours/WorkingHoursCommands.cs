namespace CrewCall.Workforce.WorkingHours;

/// <param name="DayOfWeek">English day name, e.g. "Monday" (case-insensitive).</param>
/// <param name="StartLocalTime">"HH:mm", local time in the technician's time zone.</param>
/// <param name="EndLocalTime">"HH:mm"; "24:00" (or "00:00") is the end of the day. Must be after the start.</param>
public sealed record CreateWorkingHours(Guid TechnicianId, string? DayOfWeek, string? StartLocalTime, string? EndLocalTime);

public abstract record CreateWorkingHoursOutcome
{
    private CreateWorkingHoursOutcome()
    {
    }

    public sealed record Created(TechnicianWorkingHours WorkingHours) : CreateWorkingHoursOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateWorkingHoursOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : CreateWorkingHoursOutcome;

    /// <summary>The range overlaps an existing range of the same technician on the same day.</summary>
    public sealed record Overlaps(Guid ExistingWorkingHoursId) : CreateWorkingHoursOutcome;
}

/// <summary>Deleting a range that does not exist (or belongs to another technician) also reports Deleted (idempotent).</summary>
public abstract record DeleteWorkingHoursOutcome
{
    private DeleteWorkingHoursOutcome()
    {
    }

    public sealed record Deleted : DeleteWorkingHoursOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : DeleteWorkingHoursOutcome;
}
