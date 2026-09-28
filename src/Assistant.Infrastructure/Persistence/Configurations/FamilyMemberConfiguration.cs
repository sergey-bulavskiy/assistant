using Assistant.Domain.Families;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class FamilyMemberConfiguration : IEntityTypeConfiguration<FamilyMember>
{
    public void Configure(EntityTypeBuilder<FamilyMember> builder)
    {
        builder.ToTable("family_members");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(m => m.Username).HasMaxLength(256);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(m => new { m.FamilyId, m.TelegramUserId }).IsUnique();
    }
}
