using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        var dispatchedStatuses = new[] { nameof(IncidentStatus.Dispatched), nameof(IncidentStatus.Resolved) };

        builder.ToTable("incidents", DatabaseSchemas.WorkOrders, table =>
        {
            table.HasCheckConstraint("ck_incidents_requested_start_before_end", "requested_start < requested_end");
            table.HasCheckConstraint("ck_incidents_priority", WorkOrderConfiguration.SqlIn("priority", Enum.GetNames<IncidentPriority>()));
            table.HasCheckConstraint("ck_incidents_status", WorkOrderConfiguration.SqlIn("status", Enum.GetNames<IncidentStatus>()));

            // A work order exists exactly for dispatched (and later resolved) incidents; a resolution time exactly for
            // resolved ones. No other state can carry either.
            table.HasCheckConstraint(
                "ck_incidents_work_order_when_dispatched",
                $"({WorkOrderConfiguration.SqlIn("status", dispatchedStatuses)}) = (work_order_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_incidents_resolved_at_when_resolved",
                $"(status = '{nameof(IncidentStatus.Resolved)}') = (resolved_at_utc IS NOT NULL)");
        });

        builder.HasKey(incident => incident.Id);
        builder.Property(incident => incident.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(incident => incident.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(incident => incident.SiteId).HasColumnName("site_id").IsRequired();

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(incident => incident.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // As for work orders: (site_id, customer_id) -> sites(id, customer_id), so the database itself rejects an incident
        // whose site belongs to a different customer.
        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(incident => new { incident.SiteId, incident.CustomerId })
            .HasPrincipalKey(site => new { site.Id, site.CustomerId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(incident => incident.Title).HasColumnName("title").HasMaxLength(Incident.TitleMaxLength).IsRequired();
        builder.Property(incident => incident.Description).HasColumnName("description").HasMaxLength(Incident.DescriptionMaxLength);
        builder.Property(incident => incident.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(incident => incident.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(incident => incident.RequestedStart).HasColumnName("requested_start").IsRequired();
        builder.Property(incident => incident.RequestedEnd).HasColumnName("requested_end").IsRequired();
        builder.Property(incident => incident.WorkOrderId).HasColumnName("work_order_id");
        builder.Property(incident => incident.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(incident => incident.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(incident => incident.ResolvedAtUtc).HasColumnName("resolved_at_utc");

        // The work order created by dispatch; one incident per work order at most.
        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(incident => incident.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(incident => incident.WorkOrderId)
            .IsUnique()
            .HasFilter("work_order_id IS NOT NULL")
            .HasDatabaseName("ux_incidents_work_order_id");

        builder.HasMany(incident => incident.RequiredSkills)
            .WithOne()
            .HasForeignKey(skill => skill.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(incident => incident.RequiredSkills).HasField("_requiredSkills");

        // The incident list: newest first, optionally by status or priority.
        builder.HasIndex(incident => new { incident.CreatedAtUtc, incident.Id }).HasDatabaseName("ix_incidents_created_at_utc_id");
        builder.HasIndex(incident => incident.Status).HasDatabaseName("ix_incidents_status");

        // Optimistic concurrency on PostgreSQL's xmin: of two concurrent changes (two dispatches, a dispatch and a cancel)
        // only one can win; the other fails and its whole transaction rolls back.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }
}
