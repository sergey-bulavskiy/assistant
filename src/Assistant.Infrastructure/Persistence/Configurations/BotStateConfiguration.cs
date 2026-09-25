using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class BotStateConfiguration : IEntityTypeConfiguration<BotState>
{
    public void Configure(EntityTypeBuilder<BotState> builder)
    {
        builder.ToTable("bot_state");
        builder.HasKey(b => b.BotId);
        builder.Property(b => b.BotId).ValueGeneratedNever();
        builder.Property(b => b.Username).HasMaxLength(256).IsRequired();
    }
}
