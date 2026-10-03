using Assistant.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class PendingRecordConfiguration : IEntityTypeConfiguration<PendingRecord>
{
    public void Configure(EntityTypeBuilder<PendingRecord> builder)
    {
        builder.ToTable("pending_records");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Events).HasColumnType("jsonb").IsRequired();
        builder.Property(p => p.AlertedRuleKeys).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'");
        builder.Property(p => p.Status).IsRequired();
        builder.HasIndex(p => new { p.FamilyId, p.SourceMessageId });
        // Health data is never deleted together with a profile. No navigation properties.
        builder.HasOne<HealthProfile>().WithMany().HasForeignKey(p => p.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
