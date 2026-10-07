using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoOriginalReferenceConfiguration : IEntityTypeConfiguration<VetPhotoOriginalReference>
{
    public void Configure(EntityTypeBuilder<VetPhotoOriginalReference> b)
    {
        b.ToTable("vet_photo_original_references");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => x.InputRevisionId).IsUnique();
        b.HasIndex(x => new { x.BlobId, x.State });
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.Format).HasMaxLength(10);
        b.HasOne<VetPhotoInputRevision>().WithMany().HasForeignKey(x => x.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoBlob>().WithMany().HasForeignKey(x => x.BlobId).OnDelete(DeleteBehavior.Restrict);
    }
}
