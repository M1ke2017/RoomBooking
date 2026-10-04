using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TechnicianConfiguration : IEntityTypeConfiguration<Technician>
{
    public void Configure(EntityTypeBuilder<Technician> builder)
    {
        builder.ToTable("technicians", DatabaseSchemas.Workforce);

        builder.HasKey(technician => technician.Id);
        builder.Property(technician => technician.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(technician => technician.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Technician.DisplayNameMaxLength)
            .IsRequired();

        // Stored in lower case by the Workforce module, so this index makes email unique case-insensitively.
        builder.Property(technician => technician.Email)
            .HasColumnName("email")
            .HasMaxLength(Technician.EmailMaxLength)
            .IsRequired();
        builder.HasIndex(technician => technician.Email).IsUnique();

        // No database default: with a bool default of true, EF would treat false as "unset" and the database
        // would store true. The Workforce module applies the default (active) instead.
        builder.Property(technician => technician.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        // Technician -> 0..1 Team. Restrict: a team with members cannot be deleted by accident.
        builder.Property(technician => technician.TeamId).HasColumnName("team_id");
        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(technician => technician.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        // Optimistic concurrency on PostgreSQL's xmin system column (no extra column): two concurrent team
        // assignments for the same technician cannot both win.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }
}
