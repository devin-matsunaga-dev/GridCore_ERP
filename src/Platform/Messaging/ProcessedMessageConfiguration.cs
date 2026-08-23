using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Platform.Messaging;

/// <summary>Maps <see cref="ProcessedMessage"/> onto <c>platform.processed_messages</c>.</summary>
public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processed_messages");

        // The composite key is the dedupe check: a second insert for the same pair cannot succeed,
        // so two concurrent deliveries cannot both be "the first one" however the reads interleave.
        builder.HasKey(message => new { message.MessageId, message.Consumer }).HasName("pk_processed_messages");

        builder.Property(message => message.MessageId).HasColumnName("message_id");
        builder.Property(message => message.Consumer).HasColumnName("consumer").HasMaxLength(ProcessedMessage.ConsumerNameLength);
        builder.Property(message => message.ProcessedAt).HasColumnName("processed_at").IsRequired();

        // Old rows are prunable once redelivery is no longer possible; the sweep needs this.
        builder.HasIndex(message => message.ProcessedAt).HasDatabaseName("ix_processed_messages_processed_at");
    }
}
