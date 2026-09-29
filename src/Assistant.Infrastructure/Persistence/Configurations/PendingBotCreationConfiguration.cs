using Assistant.Domain.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class PendingBotCreationConfiguration : IEntityTypeConfiguration<PendingBotCreation>
{
    public void Configure(EntityTypeBuilder<PendingBotCreation> builder)
    {
        builder.ToTable("pending_bot_creations");
        builder.HasKey(p => p.CreatorTelegramUserId);
        builder.Property(p => p.CreatorTelegramUserId).ValueGeneratedNever();
        // Same limit as bots.role, which the role is copied into.
        builder.Property(p => p.Role).HasMaxLength(64).IsRequired();
    }
}
