namespace CrewCall.Resources.Vehicles;

/// <param name="IsActive">Defaults to true when not provided.</param>
public sealed record CreateVehicle(string? RegistrationNumber, string? DisplayName, string? VehicleType, bool? IsActive);

public abstract record CreateVehicleOutcome
{
    private CreateVehicleOutcome()
    {
    }

    public sealed record Created(Vehicle Vehicle) : CreateVehicleOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateVehicleOutcome;

    public sealed record RegistrationNumberAlreadyExists(string RegistrationNumber) : CreateVehicleOutcome;
}
