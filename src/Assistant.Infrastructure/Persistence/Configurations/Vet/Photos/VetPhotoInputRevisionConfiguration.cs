using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoInputRevisionConfiguration : IEntityTypeConfiguration<VetPhotoInputRevision>
{
    public void Configure(EntityTypeBuilder<VetPhotoInputRevision> b)
    {
        b.ToTable("vet_photo_input_revisions");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.SourceId, x.Ordinal }).IsUnique();
        b.HasIndex(x => new { x.SourceId, x.UpdateId }).IsUnique();
        b.Property(x => x.FileId).HasMaxLength(2048);
        b.Property(x => x.FileUniqueId).HasMaxLength(2048);
        b.Property(x => x.FileName).HasMaxLength(256);
        b.Property(x => x.ReportedMimeType).HasMaxLength(128);
        b.Property(x => x.Caption).HasMaxLength(4096);
        b.Property(x => x.InputFingerprint).HasMaxLength(64);
        b.HasOne<VetPhotoSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoInputRevision>().WithMany().HasForeignKey(x => x.ReusesImageInputId).OnDelete(DeleteBehavior.Restrict);
    }
}
