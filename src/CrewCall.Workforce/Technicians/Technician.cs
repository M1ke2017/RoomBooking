namespace CrewCall.Workforce.Technicians;

/// <summary>The basic identity of a field technician. Skills, teams and availability come in later sprints.</summary>
public sealed class Technician
{
    public const int DisplayNameMaxLength = 200;
    public const int EmailMaxLength = 254;

    internal Technician(Guid id, string displayName, string email, bool isActive)
    {
        Id = id;
        DisplayName = displayName;
        Email = email;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>Unique, stored in lower case.</summary>
    public string Email { get; private set; }

    public bool IsActive { get; private set; }
}
