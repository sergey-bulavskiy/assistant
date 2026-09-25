using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class ChatMigrationConfiguration : IEntityTypeConfiguration<ChatMigration>
{
    public void Configure(EntityTypeBuilder<ChatMigration> builder)
    {
        builder.ToTable("chat_migrations");
        builder.HasKey(c => c.FromChatId);
        builder.Property(c => c.FromChatId).ValueGeneratedNever();
    }
}
