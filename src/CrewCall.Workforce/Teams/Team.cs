namespace CrewCall.Workforce.Teams;

/// <summary>An operational team of technicians. A technician belongs to at most one team (Technician.TeamId).</summary>
public sealed class Team
{
    public const int NameMaxLength = 100;

    internal Team(Guid id, string name, bool isActive)
    {
        Id = id;
        Name = name;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }
}
