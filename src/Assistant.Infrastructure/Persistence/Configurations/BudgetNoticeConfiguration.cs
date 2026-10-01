using Assistant.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class BudgetNoticeConfiguration : IEntityTypeConfiguration<BudgetNotice>
{
    public void Configure(EntityTypeBuilder<BudgetNotice> builder)
    {
        builder.ToTable("budget_notices");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.PeriodKind).HasMaxLength(16).IsRequired();
        builder.HasIndex(n => new { n.PeriodKind, n.PeriodStart, n.Threshold }).IsUnique();
    }
}
