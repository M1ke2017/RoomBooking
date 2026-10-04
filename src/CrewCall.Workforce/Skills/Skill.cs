namespace CrewCall.Workforce.Skills;

/// <summary>A competence a technician can have, e.g. Electrical, HVAC, Fiber.</summary>
public sealed class Skill
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 50;

    internal Skill(Guid id, string name, string? code, bool isActive)
    {
        Id = id;
        Name = name;
        Code = code;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>Optional short code, unique when present, stored in upper case.</summary>
    public string? Code { get; private set; }

    public bool IsActive { get; private set; }
}
