using CrewCall.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrewCall.Persistence.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages", DatabaseSchemas.Ops);

        // The MessageId is the key: a message is recorded once, whatever the number of deliveries.
        builder.HasKey(message => message.MessageId);
        builder.Property(message => message.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(message => message.Type).HasColumnName("type").HasMaxLength(InboxMessage.TypeMaxLength).IsRequired();
        builder.Property(message => message.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();
        builder.Property(message => message.ProcessedAtUtc).HasColumnName("processed_at_utc");
    }
}
