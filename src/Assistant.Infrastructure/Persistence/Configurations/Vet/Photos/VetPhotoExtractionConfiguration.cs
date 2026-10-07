using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoExtractionConfiguration : IEntityTypeConfiguration<VetPhotoExtraction>
{
    public void Configure(EntityTypeBuilder<VetPhotoExtraction> b)
    {
        b.ToTable("vet_photo_extractions");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => x.AttemptId).IsUnique();
        b.HasIndex(x => new { x.InputRevisionId, x.CreatedAt });
        b.Property(x => x.ModelName).HasMaxLength(200);
        b.Property(x => x.PromptVersion).HasMaxLength(30);
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.StructuredJson).HasMaxLength(16384);
        b.Property(x => x.FailureCategory).HasMaxLength(40);
        b.HasOne<VetPhotoSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoInputRevision>().WithMany().HasForeignKey(x => x.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoExtraction>().WithMany().HasForeignKey(x => x.ReusesExtractionId).OnDelete(DeleteBehavior.Restrict);
    }
}
