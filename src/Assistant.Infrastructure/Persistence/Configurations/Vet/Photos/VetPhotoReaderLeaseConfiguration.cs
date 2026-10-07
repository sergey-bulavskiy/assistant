using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoReaderLeaseConfiguration : IEntityTypeConfiguration<VetPhotoReaderLease>
{
    public void Configure(EntityTypeBuilder<VetPhotoReaderLease> b)
    {
        b.ToTable("vet_photo_reader_leases");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.BlobId, x.ReleasedAt, x.ExpiresAt });
        b.HasIndex(x => new { x.AttemptId, x.ClaimToken }).IsUnique();
        b.HasOne<VetPhotoBlob>().WithMany().HasForeignKey(x => x.BlobId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoOriginalReference>().WithMany().HasForeignKey(x => x.OriginalReferenceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
    }
}
