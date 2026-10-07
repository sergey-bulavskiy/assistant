using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoCandidateConfiguration : IEntityTypeConfiguration<VetPhotoCandidate>
{
    public void Configure(EntityTypeBuilder<VetPhotoCandidate> b)
    {
        b.ToTable("vet_photo_candidates");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.SourceId, x.CandidateOrdinal }).IsUnique();
        b.HasIndex(x => x.EventId).IsUnique().HasFilter("event_id IS NOT NULL");
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.EffectiveJson).HasMaxLength(16384);
        b.Property(x => x.CorrectionProvenanceJson).HasMaxLength(16384);
        b.Property(x => x.ReasonsJson).HasMaxLength(8192);
        b.Property(x => x.DuplicateDecision).HasMaxLength(24);
        b.HasOne<VetPhotoSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoInputRevision>().WithMany().HasForeignKey(x => x.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetPhotoExtraction>().WithMany().HasForeignKey(x => x.ExtractionResultId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Assistant.Domain.Vet.VetEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
