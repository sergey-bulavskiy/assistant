using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetExtractionResultConfiguration : IEntityTypeConfiguration<VetExtractionResult>
{
    public void Configure(EntityTypeBuilder<VetExtractionResult> b)
    {
        b.ToTable("vet_extraction_results");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.InputRevisionId).IsUnique();
        b.Property(x => x.ModelName).HasMaxLength(200);
        b.Property(x => x.PromptVersion).HasMaxLength(50);
        b.Property(x => x.Json).HasColumnType("jsonb");
        b.HasOne<VetTextSourceRevision>().WithMany().HasForeignKey(x => x.InputRevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}
