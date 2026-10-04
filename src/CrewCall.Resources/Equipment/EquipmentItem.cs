namespace CrewCall.Resources.Equipment;

/// <summary>
/// One physical piece of equipment (an asset), e.g. a fiber tester. Named EquipmentItem because "equipment"
/// is uncountable; the API resource is still /api/equipment. No quantity, warehouse or assignment data yet.
/// </summary>
public sealed class EquipmentItem
{
    public const int NameMaxLength = 200;
    public const int AssetCodeMaxLength = 50;

    internal EquipmentItem(Guid id, string name, string assetCode, bool isActive)
    {
        Id = id;
        Name = name;
        AssetCode = assetCode;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>Unique, stored in upper case.</summary>
    public string AssetCode { get; private set; }

    public bool IsActive { get; private set; }
}
