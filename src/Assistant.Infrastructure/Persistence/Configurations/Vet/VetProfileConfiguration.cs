using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetProfileConfiguration : IEntityTypeConfiguration<VetProfile>
{
    public void Configure(EntityTypeBuilder<VetProfile> b)
    {
        b.ToTable("vet_profiles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.Property(x => x.GlucoseUnit).HasMaxLength(20);
        b.Property(x => x.InsulinUnit).HasMaxLength(20);
        b.Property(x => x.InsulinProduct).HasMaxLength(100);
        b.Property(x => x.OwnerContextNote).HasMaxLength(500);
        b.Property(x => x.ReportedVetGuidance).HasMaxLength(500);
        b.Property(x => x.FieldProvenanceJson).HasColumnType("jsonb");

    }
}
