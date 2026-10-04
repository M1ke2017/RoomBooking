namespace CrewCall.Workforce.Absences;

public enum AbsenceType
{
    Vacation,
    SickLeave,
    Training,
    Other
}

/// <summary>
/// A period the technician is not available, as instants: half-open [Start, End), stored in UTC. Absences of one
/// technician never overlap, but may touch. No approval workflow: a recorded absence applies immediately.
/// </summary>
public sealed class TechnicianAbsence
{
    public const int ReasonMaxLength = 500;

    internal TechnicianAbsence(Guid id, Guid technicianId, DateTimeOffset start, DateTimeOffset end, AbsenceType type, string? reason)
    {
        Id = id;
        TechnicianId = technicianId;
        Start = start;
        End = end;
        Type = type;
        Reason = reason;
    }

    public Guid Id { get; private set; }

    public Guid TechnicianId { get; private set; }

    public DateTimeOffset Start { get; private set; }

    public DateTimeOffset End { get; private set; }

    public AbsenceType Type { get; private set; }

    public string? Reason { get; private set; }
}
