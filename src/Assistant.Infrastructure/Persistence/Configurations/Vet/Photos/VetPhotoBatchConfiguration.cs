using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoBatchConfiguration : IEntityTypeConfiguration<VetPhotoBatch>
{
    public void Configure(EntityTypeBuilder<VetPhotoBatch> b)
    {
        b.ToTable("vet_photo_batches");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.TelegramBotId, x.ChatId, x.TopicId })
            .IsUnique().AreNullsDistinct(false).HasFilter("state = 'collecting'");
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.AssumptionsJson).HasMaxLength(16384);
        b.Property(x => x.IntakeKind).HasMaxLength(12);
        b.HasOne<Assistant.Domain.Vet.VetProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
