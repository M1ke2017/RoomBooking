using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class IntegrationEventReceiptConfiguration : IEntityTypeConfiguration<IntegrationEventReceipt>
{
    public void Configure(EntityTypeBuilder<IntegrationEventReceipt> builder)
    {
        builder.ToTable("integration_event_receipts", DatabaseSchemas.Ops);

        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(receipt => receipt.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(receipt => receipt.EventType).HasColumnName("event_type").HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(receipt => receipt.EventVersion).HasColumnName("event_version").IsRequired();
        builder.Property(receipt => receipt.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(receipt => receipt.CorrelationId).HasColumnName("correlation_id");
        builder.Property(receipt => receipt.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();

        // One receipt per message: a second effect for the same message is impossible even past the inbox.
        builder.HasIndex(receipt => receipt.MessageId).IsUnique().HasDatabaseName("ux_integration_event_receipts_message_id");
    }
}
