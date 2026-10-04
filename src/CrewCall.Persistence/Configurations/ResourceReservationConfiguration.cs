using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class ResourceReservationConfiguration : IEntityTypeConfiguration<ResourceReservation>
{
    public void Configure(EntityTypeBuilder<ResourceReservation> builder)
    {
        // No overlapping reservations of the same resource: an exclusion constraint created in the
        // AddSchedulingResourceReservations migration (EF Core cannot model exclusion constraints).
        builder.ToTable("resource_reservations", DatabaseSchemas.Scheduling, table =>
        {
            table.HasCheckConstraint("ck_resource_reservations_start_before_end", "start_at < end_at");
            table.HasCheckConstraint(
                "ck_resource_reservations_resource_type", WorkOrderConfiguration.SqlIn("resource_type", Enum.GetNames<ResourceType>()));
        });

        builder.HasKey(reservation => reservation.Id);
        builder.Property(reservation => reservation.Id).HasColumnName("id").ValueGeneratedNever();

        // Polymorphic reference to a technician, vehicle or equipment asset in another module's schema: no foreign key.
        builder.Property(reservation => reservation.ResourceType).HasColumnName("resource_type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(reservation => reservation.ResourceId).HasColumnName("resource_id").IsRequired();

        // The visit lives in the workorders schema: no foreign key across module schemas.
        builder.Property(reservation => reservation.VisitId).HasColumnName("visit_id");

        builder.Property(reservation => reservation.Start).HasColumnName("start_at").IsRequired();
        builder.Property(reservation => reservation.End).HasColumnName("end_at").IsRequired();

        builder.HasIndex(reservation => new { reservation.ResourceType, reservation.ResourceId, reservation.Start, reservation.End })
            .HasDatabaseName("ix_resource_reservations_resource_period");
        builder.HasIndex(reservation => reservation.VisitId);
    }
}
