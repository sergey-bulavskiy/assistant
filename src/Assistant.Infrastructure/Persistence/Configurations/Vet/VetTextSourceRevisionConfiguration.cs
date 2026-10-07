using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetTextSourceRevisionConfiguration : IEntityTypeConfiguration<VetTextSourceRevision>
{
    public void Configure(EntityTypeBuilder<VetTextSourceRevision> b)
    {
        b.ToTable("vet_text_source_revisions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SourceId, x.Ordinal }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.UpdateId }).IsUnique();
        b.Property(x => x.Text).HasMaxLength(16000);
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.State).HasMaxLength(30);
        b.Property(x => x.FailureCategory).HasMaxLength(50);
        b.Property(x => x.AnswerState).HasMaxLength(30);

        b.HasOne<VetTextSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
