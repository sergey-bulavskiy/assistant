using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetTextSourceConfiguration : IEntityTypeConfiguration<VetTextSource>
{
    public void Configure(EntityTypeBuilder<VetTextSource> b)
    {
        b.ToTable("vet_text_sources");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.TelegramBotId, x.ChatId, x.TopicId, x.TelegramMessageId, x.SourceSlot }).IsUnique().AreNullsDistinct(false);
        b.Property(x => x.ChatType).HasMaxLength(20);


    }
}
