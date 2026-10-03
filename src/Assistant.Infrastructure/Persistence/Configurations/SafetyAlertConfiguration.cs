using Assistant.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class SafetyAlertConfiguration : IEntityTypeConfiguration<SafetyAlert>
{
    public void Configure(EntityTypeBuilder<SafetyAlert> builder)
    {
        builder.ToTable("safety_alerts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.RuleKey).IsRequired();
        builder.Property(a => a.Level).IsRequired();
        builder.Property(a => a.ThresholdSource).IsRequired();
        builder.Property(a => a.Threshold).HasPrecision(8, 2);
        // One alert per event and rule. SafetyAlertStore's raw INSERT ... ON CONFLICT (event_id, rule_key)
        // relies on exactly this unique index.
        builder.HasIndex(a => new { a.EventId, a.RuleKey }).IsUnique();
        // Events are only soft-deleted; an alerted event can never be hard-deleted. No navigation properties.
        builder.HasOne<HealthEvent>().WithMany().HasForeignKey(a => a.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
