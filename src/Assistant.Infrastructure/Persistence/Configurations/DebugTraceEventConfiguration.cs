using Assistant.Domain.Diagnostics;
using Assistant.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class DebugTraceEventConfiguration : IEntityTypeConfiguration<DebugTraceEvent>
{
    public void Configure(EntityTypeBuilder<DebugTraceEvent> builder)
    {
        builder.ToTable("debug_trace_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Stage).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ReasonCode).HasMaxLength(64);
        builder.Property(e => e.DetailJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(e => new { e.TraceId, e.Id });
        builder.HasIndex(e => e.SourceMessageId);
        builder.HasIndex(e => e.RelatedSourceMessageId);
        builder.HasIndex(e => e.LlmCallId);
        builder.HasOne<DebugTrace>().WithMany().HasForeignKey(e => e.TraceId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LlmCall>().WithMany().HasForeignKey(e => e.LlmCallId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
