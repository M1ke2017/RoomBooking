using CrewCall.Workforce.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("skills", DatabaseSchemas.Workforce);

        builder.HasKey(skill => skill.Id);
        builder.Property(skill => skill.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(skill => skill.Name).HasColumnName("name").HasMaxLength(Skill.NameMaxLength).IsRequired();

        // Upper-cased by the Workforce module, so unique case-insensitively; only enforced when a code is present.
        builder.Property(skill => skill.Code).HasColumnName("code").HasMaxLength(Skill.CodeMaxLength);
        builder.HasIndex(skill => skill.Code).IsUnique().HasFilter("code IS NOT NULL");

        builder.Property(skill => skill.IsActive).HasColumnName("is_active").IsRequired();
    }
}
