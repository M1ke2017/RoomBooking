namespace CrewCall.Contracts.Equipment;

/// <param name="IsActive">Defaults to true when omitted.</param>
public sealed record CreateEquipmentRequest(string? Name, string? AssetCode, bool? IsActive);

public sealed record EquipmentResponse(Guid Id, string Name, string AssetCode, bool IsActive);
