using Assistant.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class ChatSettingConfiguration : IEntityTypeConfiguration<ChatSetting>
{
    public void Configure(EntityTypeBuilder<ChatSetting> builder)
    {
        builder.ToTable("chat_settings");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.PreferredModel).HasMaxLength(128);
        // Pitfall: Postgres' default unique index treats every NULL as distinct, so two rows with
        // the same (BotId, ChatId) and TopicId == null would NOT collide -- AreNullsDistinct(false)
        // switches to NULLS NOT DISTINCT so a chat-wide setting (TopicId null) really is unique per
        // (bot, chat), matching spec 4's "Unique (bot, chat, topic), nulls not distinct."
        builder.HasIndex(c => new { c.BotId, c.ChatId, c.TopicId }).IsUnique().AreNullsDistinct(false);
    }
}
