namespace CrewCall.Resources.Equipment;

/// <param name="IsActive">Defaults to true when not provided.</param>
public sealed record CreateEquipment(string? Name, string? AssetCode, bool? IsActive);

public abstract record CreateEquipmentOutcome
{
    private CreateEquipmentOutcome()
    {
    }

    public sealed record Created(EquipmentItem Equipment) : CreateEquipmentOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateEquipmentOutcome;

    public sealed record AssetCodeAlreadyExists(string AssetCode) : CreateEquipmentOutcome;
}
