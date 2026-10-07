using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoRunConfiguration : IEntityTypeConfiguration<VetPhotoRun>
{
    public void Configure(EntityTypeBuilder<VetPhotoRun> b)
    {
        b.ToTable("vet_photo_runs");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.OperationKey }).IsUnique();
        b.Property(x => x.SelectionMode).HasMaxLength(24);
        b.Property(x => x.SelectionJson).HasColumnType("text");
        b.Property(x => x.ModelName).HasMaxLength(200);
        b.Property(x => x.State).HasMaxLength(24);
        b.HasOne<VetPhotoReview>().WithMany().HasForeignKey(x => x.SelectionReviewId).OnDelete(DeleteBehavior.Restrict);
    }
}
