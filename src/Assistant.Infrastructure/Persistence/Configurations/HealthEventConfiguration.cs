using Assistant.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class HealthEventConfiguration : IEntityTypeConfiguration<HealthEvent>
{
    public void Configure(EntityTypeBuilder<HealthEvent> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).IsRequired();
        builder.Property(e => e.SubjectTag).IsRequired();
        builder.Property(e => e.OccurredAtSource).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.Flags).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'");
        // Active events of a profile by type and time (newest first); deleted rows are not indexed.
        builder.HasIndex(e => new { e.FamilyId, e.ProfileId, e.Type, e.OccurredAt })
            .IsDescending(false, false, false, true)
            .HasFilter("deleted_at IS NULL");
        builder.HasIndex(e => e.SourceMessageId);
        // Health events are never deleted together with a profile. No navigation properties.
        builder.HasOne<HealthProfile>().WithMany().HasForeignKey(e => e.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
