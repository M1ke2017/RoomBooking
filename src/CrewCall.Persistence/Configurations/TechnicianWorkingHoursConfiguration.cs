using CrewCall.Workforce.Technicians;
using CrewCall.Workforce.WorkingHours;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class TechnicianWorkingHoursConfiguration : IEntityTypeConfiguration<TechnicianWorkingHours>
{
    public void Configure(EntityTypeBuilder<TechnicianWorkingHours> builder)
    {
        // The no-overlap rule per technician and day is also an exclusion constraint, created in the
        // AddWorkforceAvailability migration (EF Core cannot model exclusion constraints).
        builder.ToTable("technician_working_hours", DatabaseSchemas.Workforce, table =>
        {
            table.HasCheckConstraint("ck_technician_working_hours_day_of_week", WorkOrderConfiguration.SqlIn("day_of_week", Enum.GetNames<DayOfWeek>()));

            // End 00:00 means 24:00 (end of day); otherwise the range must end after it starts.
            table.HasCheckConstraint("ck_technician_working_hours_start_before_end", "end_local_time = '00:00' OR start_local_time < end_local_time");
        });

        builder.HasKey(range => range.Id);
        builder.Property(range => range.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(range => range.TechnicianId).HasColumnName("technician_id").IsRequired();

        // Technician 1 -> N working-hours ranges.
        builder.HasOne<Technician>()
            .WithMany()
            .HasForeignKey(range => range.TechnicianId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(range => range.DayOfWeek).HasColumnName("day_of_week").HasConversion<string>().HasMaxLength(9).IsRequired();

        // TimeOnly -> PostgreSQL "time": a local wall-clock time without date or offset.
        builder.Property(range => range.StartLocalTime).HasColumnName("start_local_time").IsRequired();
        builder.Property(range => range.EndLocalTime).HasColumnName("end_local_time").IsRequired();

        builder.Ignore(range => range.EndsAtEndOfDay);

        builder.HasIndex(range => new { range.TechnicianId, range.DayOfWeek });
    }
}
