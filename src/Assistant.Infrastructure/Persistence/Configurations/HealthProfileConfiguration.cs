using Assistant.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class HealthProfileConfiguration : IEntityTypeConfiguration<HealthProfile>
{
    public void Configure(EntityTypeBuilder<HealthProfile> builder)
    {
        builder.ToTable("health_profiles");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.SubjectTag).IsRequired().HasDefaultValue(HealthProfile.DefaultSubjectTag);
        builder.Property(p => p.TimeZone).IsRequired().HasDefaultValue(HealthProfile.DefaultTimeZone);
        builder.Property(p => p.EmergencyPhone).IsRequired().HasDefaultValue(HealthProfile.DefaultEmergencyPhone);
        // One profile per health bot (bots.id).
        builder.HasIndex(p => p.BotId).IsUnique();
    }
}
