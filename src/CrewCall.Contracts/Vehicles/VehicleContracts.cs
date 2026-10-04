namespace CrewCall.Contracts.Vehicles;

/// <param name="IsActive">Defaults to true when omitted.</param>
public sealed record CreateVehicleRequest(string? RegistrationNumber, string? DisplayName, string? VehicleType, bool? IsActive);

public sealed record VehicleResponse(Guid Id, string RegistrationNumber, string DisplayName, string? VehicleType, bool IsActive);
