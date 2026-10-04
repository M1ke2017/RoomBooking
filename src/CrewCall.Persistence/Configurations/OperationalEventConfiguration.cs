using CrewCall.Persistence.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class OperationalEventConfiguration : IEntityTypeConfiguration<OperationalEvent>
{
    public void Configure(EntityTypeBuilder<OperationalEvent> builder)
    {
        // Append-only: the migration installs a trigger that rejects UPDATE, DELETE and TRUNCATE.
        builder.ToTable("operational_events", DatabaseSchemas.Ops);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        builder.HasIndex(e => e.Sequence).IsUnique();

        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(OperationalEvent.EventTypeMaxLength).IsRequired();
        builder.Property(e => e.AggregateType).HasColumnName("aggregate_type").HasMaxLength(OperationalEvent.AggregateTypeMaxLength).IsRequired();
        builder.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
        builder.Property(e => e.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id");

        builder.HasIndex(e => new { e.AggregateType, e.AggregateId, e.OccurredAtUtc }).HasDatabaseName("ix_operational_events_aggregate");
    }
}
