using CrewCall.WorkOrders.Executions;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class VisitExecutionConfiguration : IEntityTypeConfiguration<VisitExecution>
{
    public void Configure(EntityTypeBuilder<VisitExecution> builder)
    {
        string Status(FieldWorkStatus status) => $"'{status}'";

        builder.ToTable("visit_executions", DatabaseSchemas.WorkOrders, table =>
        {
            table.HasCheckConstraint("ck_visit_executions_status", WorkOrderConfiguration.SqlIn("status", Enum.GetNames<FieldWorkStatus>()));

            // The timestamps match the status: work started for every working state, completion and cancellation exactly
            // for their terminal states, and the order travel <= work <= completion.
            table.HasCheckConstraint(
                "ck_visit_executions_work_started",
                $"status NOT IN ({Status(FieldWorkStatus.Working)}, {Status(FieldWorkStatus.Paused)}, {Status(FieldWorkStatus.Completed)}) OR work_started_at_utc IS NOT NULL");
            table.HasCheckConstraint(
                "ck_visit_executions_travel_started",
                $"status <> {Status(FieldWorkStatus.Traveling)} OR travel_started_at_utc IS NOT NULL");
            table.HasCheckConstraint(
                "ck_visit_executions_completed_at",
                $"(status = {Status(FieldWorkStatus.Completed)}) = (completed_at_utc IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_visit_executions_cancelled_at",
                $"(status = {Status(FieldWorkStatus.Cancelled)}) = (cancelled_at_utc IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_visit_executions_travel_before_work",
                "travel_started_at_utc IS NULL OR work_started_at_utc IS NULL OR travel_started_at_utc <= work_started_at_utc");
            table.HasCheckConstraint(
                "ck_visit_executions_work_before_completion",
                "work_started_at_utc IS NULL OR completed_at_utc IS NULL OR work_started_at_utc <= completed_at_utc");
        });

        builder.HasKey(execution => execution.Id);
        builder.Property(execution => execution.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(execution => execution.VisitId).HasColumnName("visit_id").IsRequired();
        builder.Property(execution => execution.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(execution => execution.TravelStartedAtUtc).HasColumnName("travel_started_at_utc");
        builder.Property(execution => execution.WorkStartedAtUtc).HasColumnName("work_started_at_utc");
        builder.Property(execution => execution.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(execution => execution.CancelledAtUtc).HasColumnName("cancelled_at_utc");
        builder.Property(execution => execution.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(execution => execution.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Visit 1 -> 0..1 VisitExecution: one execution per visit, also under concurrent first changes.
        builder.HasOne<Visit>()
            .WithOne()
            .HasForeignKey<VisitExecution>(execution => execution.VisitId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(execution => execution.VisitId).IsUnique().HasDatabaseName("ux_visit_executions_visit_id");

        builder.HasMany(execution => execution.Pauses)
            .WithOne()
            .HasForeignKey(pause => pause.VisitExecutionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(execution => execution.Pauses).HasField("_pauses");
        builder.Ignore(execution => execution.OpenPause);

        // Optimistic concurrency on PostgreSQL's xmin: of two concurrent changes (two pauses, a pause and a completion)
        // only one can win; the other fails with nothing saved.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }
}
