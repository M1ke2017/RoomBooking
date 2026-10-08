using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages", DatabaseSchemas.Ops, table =>
            table.HasCheckConstraint("ck_inbox_messages_consumer_name_not_blank", "btrim(consumer_name) <> ''"));

        // One record per consumer and message: each consumer handles a message once, whatever the number of deliveries,
        // and different consumers never block or skip each other (ADR-0015).
        builder.HasKey(message => new { message.ConsumerName, message.MessageId });
        builder.Property(message => message.ConsumerName).HasColumnName("consumer_name")
            .HasMaxLength(InboxMessage.ConsumerNameMaxLength).IsRequired();
        builder.Property(message => message.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(InboxMessage.TypeMaxLength).IsRequired();
        builder.Property(message => message.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();
        builder.Property(message => message.ProcessedAtUtc).HasColumnName("processed_at_utc");
    }
}
