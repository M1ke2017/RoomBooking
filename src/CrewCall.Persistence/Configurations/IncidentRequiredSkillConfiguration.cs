using CrewCall.WorkOrders.Incidents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class IncidentRequiredSkillConfiguration : IEntityTypeConfiguration<IncidentRequiredSkill>
{
    public void Configure(EntityTypeBuilder<IncidentRequiredSkill> builder)
    {
        builder.ToTable("incident_required_skills", DatabaseSchemas.WorkOrders, table =>
            // Normalized codes only: trimmed, upper case, not empty.
            table.HasCheckConstraint(
                "ck_incident_required_skills_skill_code_normalized",
                "skill_code <> '' AND skill_code = upper(btrim(skill_code))"));

        // One row per incident and code: a skill cannot be required twice.
        builder.HasKey(skill => new { skill.IncidentId, skill.SkillCode });
        builder.Property(skill => skill.IncidentId).HasColumnName("incident_id");
        builder.Property(skill => skill.SkillCode).HasColumnName("skill_code").HasMaxLength(Incident.SkillCodeMaxLength);
    }
}
