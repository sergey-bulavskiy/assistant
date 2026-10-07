using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoSourceConfiguration : IEntityTypeConfiguration<VetPhotoSource>
{
    public void Configure(EntityTypeBuilder<VetPhotoSource> b)
    {
        b.ToTable("vet_photo_sources");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.TelegramBotId, x.ChatId, x.TopicId, x.TelegramMessageId, x.SourceSlot })
            .IsUnique().AreNullsDistinct(false);
        b.HasIndex(x => new { x.BatchId, x.ItemNumber }).IsUnique().HasFilter("batch_id IS NOT NULL AND item_number IS NOT NULL");
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.State, x.AdmittedAt });
        b.Property(x => x.ChatType).HasMaxLength(20);
        b.Property(x => x.MediaGroupId).HasMaxLength(256);
        b.Property(x => x.Association).HasMaxLength(24);
        b.Property(x => x.State).HasMaxLength(24);
        b.HasOne<VetPhotoBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoBatch>().WithMany().HasForeignKey(x => x.ProposedBatchId).OnDelete(DeleteBehavior.Restrict);
    }
}
