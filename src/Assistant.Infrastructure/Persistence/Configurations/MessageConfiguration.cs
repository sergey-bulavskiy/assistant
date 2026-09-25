using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<StoredMessage>
{
    public void Configure(EntityTypeBuilder<StoredMessage> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.ChatType).HasMaxLength(32).IsRequired();
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.Raw).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.Username).HasMaxLength(256);
        builder.HasIndex(m => new { m.BotId, m.ChatId, m.TelegramMessageId }).IsUnique();
    }
}
