using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoBlobConfiguration : IEntityTypeConfiguration<VetPhotoBlob>
{
    public void Configure(EntityTypeBuilder<VetPhotoBlob> b)
    {
        b.ToTable("vet_photo_blobs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.ContentHash }).IsUnique();
        b.HasIndex(x => new { x.State, x.ReclaimRequestedAt });
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.Format).HasMaxLength(10);
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.Content).HasColumnType("bytea");
    }
}
