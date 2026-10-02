using Assistant.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class SafetyRuleConfiguration : IEntityTypeConfiguration<SafetyRule>
{
    public void Configure(EntityTypeBuilder<SafetyRule> builder)
    {
        builder.ToTable("safety_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RuleKey).IsRequired();
        builder.Property(r => r.LowUrgent).HasPrecision(8, 2);
        builder.Property(r => r.LowAlert).HasPrecision(8, 2);
        builder.Property(r => r.TargetHigh).HasPrecision(8, 2);
        builder.Property(r => r.HighAlert).HasPrecision(8, 2);
        builder.Property(r => r.HighUrgent).HasPrecision(8, 2);
        builder.Property(r => r.Source).IsRequired();
        builder.HasIndex(r => new { r.ProfileId, r.RuleKey }).IsUnique();
        // Rules belong to their profile and are deleted with it. No navigation properties.
        builder.HasOne<HealthProfile>().WithMany().HasForeignKey(r => r.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}
