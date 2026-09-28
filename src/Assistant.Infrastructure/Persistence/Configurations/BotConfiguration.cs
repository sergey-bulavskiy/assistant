using Assistant.Domain.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class BotConfiguration : IEntityTypeConfiguration<Bot>
{
    public void Configure(EntityTypeBuilder<Bot> builder)
    {
        builder.ToTable("bots");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Username).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Role).HasMaxLength(64).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(b => b.TelegramBotId).IsUnique();
    }
}
