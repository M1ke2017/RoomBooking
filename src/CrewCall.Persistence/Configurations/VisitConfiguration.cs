using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    public void Configure(EntityTypeBuilder<Visit> builder)
    {
        builder.ToTable("visits", DatabaseSchemas.WorkOrders, table =>
        {
            table.HasCheckConstraint("ck_visits_start_before_end", "start_at < end_at");
            table.HasCheckConstraint("ck_visits_status", WorkOrderConfiguration.SqlIn("status", Enum.GetNames<VisitStatus>()));
        });

        builder.HasKey(visit => visit.Id);
        builder.Property(visit => visit.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(visit => visit.WorkOrderId).HasColumnName("work_order_id").IsRequired();

        // WorkOrder 1 -> N Visits.
        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(visit => visit.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(visit => visit.Start).HasColumnName("start_at").IsRequired();
        builder.Property(visit => visit.End).HasColumnName("end_at").IsRequired();
        builder.Property(visit => visit.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(visit => visit.Notes).HasColumnName("notes").HasMaxLength(Visit.NotesMaxLength);
        builder.Property(visit => visit.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        // Optimistic concurrency on PostgreSQL's xmin: two concurrent status changes cannot both win.
        builder.Property<uint>("RowVersion").IsRowVersion();
    }
}
