namespace CrewCall.Workforce.Technicians;

/// <param name="IsActive">Defaults to true when not provided.</param>
public sealed record CreateTechnician(string? DisplayName, string? Email, bool? IsActive);

public abstract record CreateTechnicianOutcome
{
    private CreateTechnicianOutcome()
    {
    }

    public sealed record Created(Technician Technician) : CreateTechnicianOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateTechnicianOutcome;

    public sealed record EmailAlreadyExists(string Email) : CreateTechnicianOutcome;
}
