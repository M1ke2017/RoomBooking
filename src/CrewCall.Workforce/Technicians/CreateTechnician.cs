namespace CrewCall.Workforce.Technicians;

/// <param name="IsActive">Defaults to true when not provided.</param>
/// <param name="TimeZoneId">IANA time zone id, e.g. "Europe/Warsaw". Required.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, e.g. "PL" (case-insensitive, stored in upper case). Required.</param>
public sealed record CreateTechnician(string? DisplayName, string? Email, bool? IsActive, string? TimeZoneId, string? CountryCode);

public abstract record CreateTechnicianOutcome
{
    private CreateTechnicianOutcome()
    {
    }

    public sealed record Created(Technician Technician) : CreateTechnicianOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateTechnicianOutcome;

    public sealed record EmailAlreadyExists(string Email) : CreateTechnicianOutcome;
}
