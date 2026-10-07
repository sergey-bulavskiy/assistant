using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoAttemptConfiguration : IEntityTypeConfiguration<VetPhotoAttempt>
{
    public void Configure(EntityTypeBuilder<VetPhotoAttempt> b)
    {
        b.ToTable("vet_photo_attempts");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.Kind, x.State, x.RetryNotBefore, x.CreatedAt });
        b.HasIndex(x => x.InputRevisionId);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.FailureCategory).HasMaxLength(40);
        b.HasOne<VetPhotoSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoInputRevision>().WithMany().HasForeignKey(x => x.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoRunWindow>().WithMany().HasForeignKey(x => x.RunWindowId).OnDelete(DeleteBehavior.Restrict);
    }
}
