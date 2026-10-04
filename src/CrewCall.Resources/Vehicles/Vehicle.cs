namespace CrewCall.Resources.Vehicles;

/// <summary>A vehicle used for field work. No GPS, maintenance or assignment data yet.</summary>
public sealed class Vehicle
{
    public const int RegistrationNumberMaxLength = 20;
    public const int DisplayNameMaxLength = 200;
    public const int VehicleTypeMaxLength = 50;

    internal Vehicle(Guid id, string registrationNumber, string displayName, string? vehicleType, bool isActive)
    {
        Id = id;
        RegistrationNumber = registrationNumber;
        DisplayName = displayName;
        VehicleType = vehicleType;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, stored in upper case.</summary>
    public string RegistrationNumber { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>Free-form type, e.g. "Van". Not an enumeration yet.</summary>
    public string? VehicleType { get; private set; }

    public bool IsActive { get; private set; }
}
