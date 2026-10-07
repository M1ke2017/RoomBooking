using CrewCall.WorkOrders.Executions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class VisitExecutionPauseConfiguration : IEntityTypeConfiguration<VisitExecutionPause>
{
    public void Configure(EntityTypeBuilder<VisitExecutionPause> builder)
    {
        builder.ToTable("visit_execution_pauses", DatabaseSchemas.WorkOrders, table =>
            table.HasCheckConstraint(
                "ck_visit_execution_pauses_start_before_end", "ended_at_utc IS NULL OR started_at_utc < ended_at_utc"));

        builder.HasKey(pause => pause.Id);
        builder.Property(pause => pause.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(pause => pause.VisitExecutionId).HasColumnName("visit_execution_id").IsRequired();
        builder.Property(pause => pause.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(pause => pause.EndedAtUtc).HasColumnName("ended_at_utc");

        builder.HasIndex(pause => new { pause.VisitExecutionId, pause.StartedAtUtc })
            .HasDatabaseName("ix_visit_execution_pauses_execution_started");

        // At most one open pause per execution, whatever the application does.
        builder.HasIndex(pause => pause.VisitExecutionId)
            .IsUnique()
            .HasFilter("ended_at_utc IS NULL")
            .HasDatabaseName("ux_visit_execution_pauses_one_open");
    }
}
