using CrewCall.Workforce.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams", DatabaseSchemas.Workforce);

        builder.HasKey(team => team.Id);
        builder.Property(team => team.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(team => team.Name).HasColumnName("name").HasMaxLength(Team.NameMaxLength).IsRequired();
        builder.Property(team => team.IsActive).HasColumnName("is_active").IsRequired();
    }
}
