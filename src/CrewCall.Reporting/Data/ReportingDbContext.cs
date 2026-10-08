using Microsoft.EntityFrameworkCore;

namespace CrewCall.Reporting.Data;

/// <summary>
/// The reporting service's own database (crewcall_reporting, schema "reporting", ADR-0016). It shares nothing with the
/// operational CrewCallDbContext: no entities, no migrations, no connection string.
/// </summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public const string Schema = "reporting";

    /// <summary>The connection string Aspire supplies for the crewcall-reporting database resource.</summary>
    public const string ConnectionStringName = "crewcall-reporting";

    public DbSet<ReportingInboxMessage> InboxMessages => Set<ReportingInboxMessage>();

    public DbSet<TechnicianActivityDaily> TechnicianActivityDays => Set<TechnicianActivityDaily>();

    public DbSet<VisitActivity> VisitActivities => Set<VisitActivity>();

    public DbSet<AssignmentActivity> AssignmentActivities => Set<AssignmentActivity>();

    public DbSet<IncidentActivity> IncidentActivities => Set<IncidentActivity>();

    public DbSet<ProjectionCheckpoint> ProjectionCheckpoints => Set<ProjectionCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ReportingInboxMessage>(inbox =>
        {
            inbox.ToTable("inbox_messages", table =>
                table.HasCheckConstraint("ck_inbox_messages_consumer_name_not_blank", "btrim(consumer_name) <> ''"));
            inbox.HasKey(message => new { message.ConsumerName, message.MessageId });
            inbox.Property(message => message.ConsumerName).HasColumnName("consumer_name").HasMaxLength(ReportingInboxMessage.ConsumerNameMaxLength);
            inbox.Property(message => message.MessageId).HasColumnName("message_id");
            inbox.Property(message => message.Type).HasColumnName("type").HasMaxLength(ReportingInboxMessage.TypeMaxLength);
            inbox.Property(message => message.Version).HasColumnName("version");
            inbox.Property(message => message.ReceivedAtUtc).HasColumnName("received_at_utc");
            inbox.Property(message => message.ProcessedAtUtc).HasColumnName("processed_at_utc");
        });

        modelBuilder.Entity<TechnicianActivityDaily>(daily =>
        {
            daily.ToTable("technician_activity", table =>
            {
                table.HasCheckConstraint("ck_technician_activity_counts",
                    "completed_visits >= 0 AND assignment_count >= 0 AND incident_dispatch_count >= 0");
                table.HasCheckConstraint("ck_technician_activity_minutes",
                    "travel_minutes >= 0 AND gross_work_minutes >= 0 AND pause_minutes >= 0 AND net_work_minutes >= 0");
            });
            // The key is the report's access path: one technician over a range of days.
            daily.HasKey(row => new { row.TechnicianId, row.DateUtc });
            daily.Property(row => row.TechnicianId).HasColumnName("technician_id");
            daily.Property(row => row.DateUtc).HasColumnName("date_utc");
            daily.Property(row => row.CompletedVisits).HasColumnName("completed_visits");
            daily.Property(row => row.AssignmentCount).HasColumnName("assignment_count");
            daily.Property(row => row.IncidentDispatchCount).HasColumnName("incident_dispatch_count");
            daily.Property(row => row.TravelMinutes).HasColumnName("travel_minutes").HasPrecision(14, 4);
            daily.Property(row => row.GrossWorkMinutes).HasColumnName("gross_work_minutes").HasPrecision(14, 4);
            daily.Property(row => row.PauseMinutes).HasColumnName("pause_minutes").HasPrecision(14, 4);
            daily.Property(row => row.NetWorkMinutes).HasColumnName("net_work_minutes").HasPrecision(14, 4);
            daily.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });

        modelBuilder.Entity<VisitActivity>(visit =>
        {
            visit.ToTable("visit_activity");
            visit.HasKey(row => row.VisitId);
            visit.Property(row => row.VisitId).HasColumnName("visit_id").ValueGeneratedNever();
            visit.Property(row => row.WorkOrderId).HasColumnName("work_order_id");
            visit.Property(row => row.CustomerId).HasColumnName("customer_id");
            visit.Property(row => row.SiteId).HasColumnName("site_id");
            visit.Property(row => row.WorkOrderPriority).HasColumnName("work_order_priority").HasMaxLength(VisitActivity.PriorityMaxLength);
            visit.Property(row => row.TechnicianId).HasColumnName("technician_id");
            visit.Property(row => row.PlannedStartUtc).HasColumnName("planned_start_utc");
            visit.Property(row => row.PlannedEndUtc).HasColumnName("planned_end_utc");
            visit.Property(row => row.PlannedChangedAtUtc).HasColumnName("planned_changed_at_utc");
            visit.Property(row => row.RescheduleCount).HasColumnName("reschedule_count");
            visit.Property(row => row.TotalDelayMinutes).HasColumnName("total_delay_minutes");
            visit.Property(row => row.ExecutionId).HasColumnName("execution_id");
            visit.Property(row => row.ActualTravelMinutes).HasColumnName("actual_travel_minutes").HasPrecision(14, 4);
            visit.Property(row => row.ActualGrossWorkMinutes).HasColumnName("actual_gross_work_minutes").HasPrecision(14, 4);
            visit.Property(row => row.ActualPauseMinutes).HasColumnName("actual_pause_minutes").HasPrecision(14, 4);
            visit.Property(row => row.ActualNetWorkMinutes).HasColumnName("actual_net_work_minutes").HasPrecision(14, 4);
            visit.Property(row => row.VisitStatus).HasColumnName("visit_status").HasMaxLength(VisitActivity.StatusMaxLength);
            visit.Property(row => row.StatusChangedAtUtc).HasColumnName("status_changed_at_utc");
            visit.Property(row => row.CompletedAtUtc).HasColumnName("completed_at_utc");
            visit.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");

            // The visit report filters by technician or site over a planned period; the daily recompute by completion.
            visit.HasIndex(row => new { row.TechnicianId, row.PlannedStartUtc }).HasDatabaseName("ix_visit_activity_technician_planned_start");
            visit.HasIndex(row => new { row.SiteId, row.PlannedStartUtc }).HasDatabaseName("ix_visit_activity_site_planned_start");
            visit.HasIndex(row => new { row.TechnicianId, row.CompletedAtUtc }).HasDatabaseName("ix_visit_activity_technician_completed_at");
        });

        modelBuilder.Entity<AssignmentActivity>(assignment =>
        {
            assignment.ToTable("assignment_activity", table => table.HasCheckConstraint(
                "ck_assignment_activity_status", "status IN ('Active', 'Replaced', 'Cancelled')"));
            assignment.HasKey(row => row.AssignmentId);
            assignment.Property(row => row.AssignmentId).HasColumnName("assignment_id").ValueGeneratedNever();
            assignment.Property(row => row.VisitId).HasColumnName("visit_id");
            assignment.Property(row => row.TechnicianId).HasColumnName("technician_id");
            assignment.Property(row => row.VehicleId).HasColumnName("vehicle_id");
            assignment.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc");
            assignment.Property(row => row.Status).HasColumnName("status").HasMaxLength(AssignmentActivity.StatusMaxLength);
            assignment.Property(row => row.EndedAtUtc).HasColumnName("ended_at_utc");
            assignment.Property(row => row.ReplacedByAssignmentId).HasColumnName("replaced_by_assignment_id");
            assignment.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");

            assignment.HasIndex(row => row.VisitId).HasDatabaseName("ix_assignment_activity_visit");
            assignment.HasIndex(row => new { row.TechnicianId, row.CreatedAtUtc }).HasDatabaseName("ix_assignment_activity_technician_created_at");
        });

        modelBuilder.Entity<IncidentActivity>(incident =>
        {
            incident.ToTable("incident_activity");
            incident.HasKey(row => row.IncidentId);
            incident.Property(row => row.IncidentId).HasColumnName("incident_id").ValueGeneratedNever();
            incident.Property(row => row.WorkOrderId).HasColumnName("work_order_id");
            incident.Property(row => row.VisitId).HasColumnName("visit_id");
            incident.Property(row => row.AssignmentId).HasColumnName("assignment_id");
            incident.Property(row => row.TechnicianId).HasColumnName("technician_id");
            incident.Property(row => row.DispatchedAtUtc).HasColumnName("dispatched_at_utc");
            incident.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");

            incident.HasIndex(row => new { row.TechnicianId, row.DispatchedAtUtc }).HasDatabaseName("ix_incident_activity_technician_dispatched_at");
        });

        modelBuilder.Entity<ProjectionCheckpoint>(checkpoint =>
        {
            checkpoint.ToTable("projection_checkpoints");
            checkpoint.HasKey(row => row.ProjectionName);
            checkpoint.Property(row => row.ProjectionName).HasColumnName("projection_name").HasMaxLength(ProjectionCheckpoint.NameMaxLength);
            checkpoint.Property(row => row.LastEventOccurredAtUtc).HasColumnName("last_event_occurred_at_utc");
            checkpoint.Property(row => row.LastProcessedAtUtc).HasColumnName("last_processed_at_utc");
            checkpoint.Property(row => row.ProcessedMessages).HasColumnName("processed_messages");
        });
    }

    /// <summary>The EF options for the reporting database: Npgsql, migration history in the reporting schema.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));
}
