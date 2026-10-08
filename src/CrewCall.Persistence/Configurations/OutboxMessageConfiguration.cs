using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", DatabaseSchemas.Ops, table =>
        {
            table.HasCheckConstraint("ck_outbox_messages_type", "btrim(type) <> ''");
            table.HasCheckConstraint("ck_outbox_messages_version", "version > 0");
            table.HasCheckConstraint("ck_outbox_messages_attempt_count", "attempt_count >= 0");
            // A message is processed or dead-lettered, never both.
            table.HasCheckConstraint("ck_outbox_messages_processed_or_failed", "processed_at_utc IS NULL OR failed_at_utc IS NULL");
        });

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(message => message.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(message => message.Version).HasColumnName("version").IsRequired();
        builder.Property(message => message.RoutingKey).HasColumnName("routing_key").HasMaxLength(OutboxMessage.RoutingKeyMaxLength).IsRequired();
        builder.Property(message => message.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CorrelationId).HasColumnName("correlation_id");
        builder.Property(message => message.AggregateType).HasColumnName("aggregate_type").HasMaxLength(OutboxMessage.AggregateTypeMaxLength);
        builder.Property(message => message.AggregateId).HasColumnName("aggregate_id");
        builder.Property(message => message.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(message => message.ProcessedAtUtc).HasColumnName("processed_at_utc");
        builder.Property(message => message.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(message => message.LastAttemptAtUtc).HasColumnName("last_attempt_at_utc");
        builder.Property(message => message.LastError).HasColumnName("last_error").HasMaxLength(OutboxMessage.LastErrorMaxLength);
        builder.Property(message => message.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
        builder.Property(message => message.LockedUntilUtc).HasColumnName("locked_until_utc");
        builder.Property(message => message.LockedBy).HasColumnName("locked_by").HasMaxLength(OutboxMessage.LockedByMaxLength);
        builder.Property(message => message.FailedAtUtc).HasColumnName("failed_at_utc");

        // The publisher's queue: only pending messages, oldest first. Processed and failed messages are history.
        builder.HasIndex(message => new { message.CreatedAtUtc, message.Id })
            .HasFilter("processed_at_utc IS NULL AND failed_at_utc IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending");
        builder.HasIndex(message => new { message.AggregateType, message.AggregateId })
            .HasDatabaseName("ix_outbox_messages_aggregate");
    }
}
