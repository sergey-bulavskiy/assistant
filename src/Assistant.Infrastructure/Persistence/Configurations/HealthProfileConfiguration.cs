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
        builder.Property(p => p.Conditions).HasMaxLength(1000);
        builder.Property(p => p.Medications).HasMaxLength(1000);
        builder.Property(p => p.Allergies).HasMaxLength(1000);
        builder.Property(p => p.DoctorPlan).HasMaxLength(1000);
        builder.Property(p => p.DoctorContacts).HasMaxLength(1000);
        // One profile per health bot (bots.id).
        builder.HasIndex(p => p.BotId).IsUnique();
    }
}
