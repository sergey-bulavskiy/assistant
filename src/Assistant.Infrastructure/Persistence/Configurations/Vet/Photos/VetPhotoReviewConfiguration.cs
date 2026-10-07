using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoReviewConfiguration : IEntityTypeConfiguration<VetPhotoReview>
{
    public void Configure(EntityTypeBuilder<VetPhotoReview> b)
    {
        b.ToTable("vet_photo_reviews");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.OperationKey }).IsUnique();
        b.HasIndex(x => new { x.BatchId, x.State, x.CreatedAt });
        b.Property(x => x.Kind).HasMaxLength(30);
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.SelectionJson).HasColumnType("text");
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.PreviewPagesJson).HasMaxLength(4194304);
        b.Property(x => x.DeliveredPagesJson).HasMaxLength(65536);
        b.Property(x => x.OutcomeJson).HasMaxLength(2097152);
        b.HasOne<Assistant.Domain.Vet.VetProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoRunWindow>().WithMany().HasForeignKey(x => x.RunWindowId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Assistant.Domain.Vet.VetDiaryAction>().WithMany().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Restrict);
    }
}
