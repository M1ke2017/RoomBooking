namespace CrewCall.Workforce.Skills;

/// <summary>Links a technician to a skill (Technician N &lt;-&gt; N Skill). No level or certification data yet.</summary>
public sealed class TechnicianSkill
{
    internal TechnicianSkill(Guid technicianId, Guid skillId)
    {
        TechnicianId = technicianId;
        SkillId = skillId;
    }

    public Guid TechnicianId { get; private set; }

    public Guid SkillId { get; private set; }
}
