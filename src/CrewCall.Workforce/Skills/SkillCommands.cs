namespace CrewCall.Workforce.Skills;

/// <param name="IsActive">Defaults to true when not provided.</param>
public sealed record CreateSkill(string? Name, string? Code, bool? IsActive);

public abstract record CreateSkillOutcome
{
    private CreateSkillOutcome()
    {
    }

    public sealed record Created(Skill Skill) : CreateSkillOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateSkillOutcome;

    public sealed record CodeAlreadyExists(string Code) : CreateSkillOutcome;
}

/// <summary>Assigning a skill is idempotent: assigning an already assigned skill reports AlreadyAssigned and changes nothing.</summary>
public abstract record AssignSkillOutcome
{
    private AssignSkillOutcome()
    {
    }

    public sealed record Assigned(Skill Skill) : AssignSkillOutcome;

    public sealed record AlreadyAssigned(Skill Skill) : AssignSkillOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : AssignSkillOutcome;

    public sealed record SkillNotFound(Guid SkillId) : AssignSkillOutcome;
}

/// <summary>Removing a skill is idempotent: removing a skill the technician does not have also reports Removed.</summary>
public abstract record RemoveSkillOutcome
{
    private RemoveSkillOutcome()
    {
    }

    public sealed record Removed : RemoveSkillOutcome;

    public sealed record TechnicianNotFound(Guid TechnicianId) : RemoveSkillOutcome;

    public sealed record SkillNotFound(Guid SkillId) : RemoveSkillOutcome;
}
