using Assistant.Domain.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class PlaceConfiguration : IEntityTypeConfiguration<Place>
{
    public void Configure(EntityTypeBuilder<Place> builder)
    {
        builder.ToTable("places");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Title).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(p => new { p.BotId, p.ChatId, p.TopicId }).IsUnique();
    }
}
