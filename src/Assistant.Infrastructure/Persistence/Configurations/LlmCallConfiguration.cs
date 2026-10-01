using Assistant.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class LlmCallConfiguration : IEntityTypeConfiguration<LlmCall>
{
    public void Configure(EntityTypeBuilder<LlmCall> builder)
    {
        builder.ToTable("llm_calls");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Tier).HasMaxLength(32).IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Model).HasMaxLength(128).IsRequired();
        builder.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.ReportedCost).HasColumnType("numeric(10,4)");
        builder.Property(c => c.Cost).HasColumnType("numeric(10,4)").IsRequired();
        builder.HasIndex(c => new { c.FamilyId, c.CreatedAt });
    }
}
