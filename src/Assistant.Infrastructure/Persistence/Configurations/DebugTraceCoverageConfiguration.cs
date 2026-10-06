using Assistant.Domain.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class DebugTraceCoverageConfiguration : IEntityTypeConfiguration<DebugTraceCoverage>
{
    public void Configure(EntityTypeBuilder<DebugTraceCoverage> builder)
    {
        builder.ToTable("debug_trace_coverage");
        builder.HasKey(c => c.Id);
    }
}
