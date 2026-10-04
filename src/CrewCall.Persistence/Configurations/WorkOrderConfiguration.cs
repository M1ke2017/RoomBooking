using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("work_orders", DatabaseSchemas.WorkOrders, table =>
        {
            table.HasCheckConstraint("ck_work_orders_priority", SqlIn("priority", Enum.GetNames<WorkOrderPriority>()));
            table.HasCheckConstraint("ck_work_orders_status", SqlIn("status", Enum.GetNames<WorkOrderStatus>()));
        });

        builder.HasKey(workOrder => workOrder.Id);
        builder.Property(workOrder => workOrder.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(workOrder => workOrder.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(workOrder => workOrder.SiteId).HasColumnName("site_id").IsRequired();

        // Customer 1 -> N WorkOrders.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(workOrder => workOrder.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Site 1 -> N WorkOrders. The composite key (site_id, customer_id) -> sites(id, customer_id) makes the database
        // itself reject a work order whose site belongs to a different customer.
        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(workOrder => new { workOrder.SiteId, workOrder.CustomerId })
            .HasPrincipalKey(site => new { site.Id, site.CustomerId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(workOrder => workOrder.Title).HasColumnName("title").HasMaxLength(WorkOrder.TitleMaxLength).IsRequired();
        builder.Property(workOrder => workOrder.Description).HasColumnName("description").HasMaxLength(WorkOrder.DescriptionMaxLength);
        builder.Property(workOrder => workOrder.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(workOrder => workOrder.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(workOrder => workOrder.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        // Optimistic concurrency on PostgreSQL's xmin: two concurrent status changes cannot both win.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }

    internal static string SqlIn(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(value => $"'{value}'"))})";
}
