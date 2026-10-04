using CrewCall.Scheduling.Assignments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class AssignmentConfiguration : IEntityTypeConfiguration<Assignment>
{
    public void Configure(EntityTypeBuilder<Assignment> builder)
    {
        builder.ToTable("assignments", DatabaseSchemas.Scheduling, table =>
        {
            table.HasCheckConstraint("ck_assignments_status", WorkOrderConfiguration.SqlIn("status", Enum.GetNames<AssignmentStatus>()));
            table.HasCheckConstraint("ck_assignments_travel_buffers", "travel_buffer_before_minutes >= 0 AND travel_buffer_after_minutes >= 0");
            table.HasCheckConstraint("ck_assignments_claimed_window", "claimed_start < claimed_end");
        });

        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.Id).HasColumnName("id").ValueGeneratedNever();

        // References into other modules' schemas (workorders.visits, workforce.technicians, resources.vehicles):
        // no foreign keys, as for resource reservations (ADR-0001, ADR-0008).
        builder.Property(assignment => assignment.VisitId).HasColumnName("visit_id").IsRequired();
        builder.Property(assignment => assignment.TechnicianId).HasColumnName("technician_id").IsRequired();
        builder.Property(assignment => assignment.VehicleId).HasColumnName("vehicle_id");

        builder.Property(assignment => assignment.TravelBufferBeforeMinutes).HasColumnName("travel_buffer_before_minutes").IsRequired();
        builder.Property(assignment => assignment.TravelBufferAfterMinutes).HasColumnName("travel_buffer_after_minutes").IsRequired();
        builder.Property(assignment => assignment.ClaimedStart).HasColumnName("claimed_start").IsRequired();
        builder.Property(assignment => assignment.ClaimedEnd).HasColumnName("claimed_end").IsRequired();
        builder.Property(assignment => assignment.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        // Not a foreign key: the old assignment is marked Replaced before its replacement row is inserted, in one transaction.
        builder.Property(assignment => assignment.ReplacedByAssignmentId).HasColumnName("replaced_by_assignment_id");

        builder.Property(assignment => assignment.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(assignment => assignment.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        builder.HasMany(assignment => assignment.Equipment)
            .WithOne()
            .HasForeignKey(equipment => equipment.AssignmentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(assignment => assignment.Equipment).UsePropertyAccessMode(PropertyAccessMode.Field);

        // At most one Active assignment per visit; any number of Replaced/Cancelled ones (the history).
        builder.HasIndex(assignment => assignment.VisitId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_assignments_one_active_per_visit");
        builder.HasIndex(assignment => new { assignment.VisitId, assignment.CreatedAtUtc })
            .HasDatabaseName("ix_assignments_visit_history");

        // Optimistic concurrency on PostgreSQL's xmin: a concurrent reassign/cancel of the same assignment loses.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }
}
