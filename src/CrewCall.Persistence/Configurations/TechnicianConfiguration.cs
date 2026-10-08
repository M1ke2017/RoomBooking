using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TechnicianConfiguration : IEntityTypeConfiguration<Technician>
{
    public void Configure(EntityTypeBuilder<Technician> builder)
    {
        builder.ToTable("technicians", DatabaseSchemas.Workforce, table =>
        {
            table.HasCheckConstraint("ck_technicians_country_code", "country_code ~ '^[A-Z]{2}$'");
            table.HasCheckConstraint("ck_technicians_phone_number_e164", "phone_number ~ '^\\+[1-9][0-9]{6,14}$'");
        });

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

        // IANA id, validated against the TZDB by the Workforce module (the database cannot check zone ids).
        builder.Property(technician => technician.TimeZoneId)
            .HasColumnName("timezone_id")
            .HasMaxLength(Technician.TimeZoneIdMaxLength)
            .IsRequired();

        builder.Property(technician => technician.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(Technician.CountryCodeLength)
            .IsFixedLength()
            .IsRequired();

        // E.164, normalized by the Workforce module; optional.
        builder.Property(technician => technician.PhoneNumber)
            .HasColumnName("phone_number")
            .HasMaxLength(Technician.PhoneNumberMaxLength);

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
