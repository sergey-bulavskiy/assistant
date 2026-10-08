using Assistant.Domain.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class GeneralMemoryFactConfiguration : IEntityTypeConfiguration<GeneralMemoryFact>
{
    public void Configure(EntityTypeBuilder<GeneralMemoryFact> b)
    {
        b.ToTable("general_memory_facts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Text).HasMaxLength(500).IsRequired();
        b.Property(x => x.Tag).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.FamilyId, x.BotId, x.SourceMessageId }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotId, x.ChatId, x.TopicId });
    }
}
