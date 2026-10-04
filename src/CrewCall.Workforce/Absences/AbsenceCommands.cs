namespace CrewCall.Workforce.Absences;

/// <param name="Type">Vacation, SickLeave, Training or Other.</param>
public sealed record CreateAbsence(Guid TechnicianId, DateTimeOffset? Start, DateTimeOffset? End, string? Type, string? Reason);

public abstract record CreateAbsenceOutcome
{
    private CreateAbsenceOutcome()
    {
    }

    public sealed record Created(TechnicianAbsence Absence) : CreateAbsenceOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateAbsenceOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : CreateAbsenceOutcome;

    /// <summary>The period overlaps an existing absence of the same technician.</summary>
    public sealed record Overlaps(Guid ExistingAbsenceId) : CreateAbsenceOutcome;
}

/// <summary>Deleting an absence that does not exist (or belongs to another technician) also reports Deleted (idempotent).</summary>
public abstract record DeleteAbsenceOutcome
{
    private DeleteAbsenceOutcome()
    {
    }

    public sealed record Deleted : DeleteAbsenceOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : DeleteAbsenceOutcome;
}
