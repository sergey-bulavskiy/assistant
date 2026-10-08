using Assistant.Domain.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class GeneralMemoryStateConfiguration : IEntityTypeConfiguration<GeneralMemoryState>
{
    public void Configure(EntityTypeBuilder<GeneralMemoryState> b)
    {
        b.ToTable("general_memory_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.SummaryText).HasMaxLength(3000).IsRequired();
        b.Property(x => x.SourceFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.ModelName).HasMaxLength(256);
        b.HasIndex(x => new { x.FamilyId, x.BotId, x.ChatId, x.TopicId }).IsUnique().AreNullsDistinct(false);
    }
}
