using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TechnicianAbsenceConfiguration : IEntityTypeConfiguration<TechnicianAbsence>
{
    public void Configure(EntityTypeBuilder<TechnicianAbsence> builder)
    {
        // The no-overlap rule per technician is also an exclusion constraint, created in the AddWorkforceAvailability
        // migration (EF Core cannot model exclusion constraints).
        builder.ToTable("technician_absences", DatabaseSchemas.Workforce, table =>
        {
            table.HasCheckConstraint("ck_technician_absences_start_before_end", "start_at < end_at");
            table.HasCheckConstraint("ck_technician_absences_type", WorkOrderConfiguration.SqlIn("type", Enum.GetNames<AbsenceType>()));
        });

        builder.HasKey(absence => absence.Id);
        builder.Property(absence => absence.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(absence => absence.TechnicianId).HasColumnName("technician_id").IsRequired();

        // Technician 1 -> N absences.
        builder.HasOne<Technician>()
            .WithMany()
            .HasForeignKey(absence => absence.TechnicianId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(absence => absence.Start).HasColumnName("start_at").IsRequired();
        builder.Property(absence => absence.End).HasColumnName("end_at").IsRequired();
        builder.Property(absence => absence.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(absence => absence.Reason).HasColumnName("reason").HasMaxLength(TechnicianAbsence.ReasonMaxLength);

        builder.HasIndex(absence => new { absence.TechnicianId, absence.Start, absence.End });
    }
}
