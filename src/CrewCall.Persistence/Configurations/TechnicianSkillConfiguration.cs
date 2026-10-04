using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TechnicianSkillConfiguration : IEntityTypeConfiguration<TechnicianSkill>
{
    public void Configure(EntityTypeBuilder<TechnicianSkill> builder)
    {
        builder.ToTable("technician_skills", DatabaseSchemas.Workforce);

        // The composite primary key is the (technician_id, skill_id) uniqueness: no duplicate links.
        builder.HasKey(link => new { link.TechnicianId, link.SkillId });
        builder.Property(link => link.TechnicianId).HasColumnName("technician_id");
        builder.Property(link => link.SkillId).HasColumnName("skill_id");

        // Restrict: deleting a skill or technician must not silently drop competence data.
        builder.HasOne<Technician>()
            .WithMany()
            .HasForeignKey(link => link.TechnicianId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Skill>()
            .WithMany()
            .HasForeignKey(link => link.SkillId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
