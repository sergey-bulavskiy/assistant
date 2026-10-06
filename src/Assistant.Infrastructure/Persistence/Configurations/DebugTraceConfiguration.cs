using Assistant.Domain.Diagnostics;
using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class DebugTraceConfiguration : IEntityTypeConfiguration<DebugTrace>
{
    public void Configure(EntityTypeBuilder<DebugTrace> builder)
    {
        builder.ToTable("debug_traces");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).HasMaxLength(32).IsRequired();
        builder.Property(t => t.BuildIdentity).HasMaxLength(64).IsRequired();
        builder.Property(t => t.FinalOutcome).HasMaxLength(32);
        builder.Property(t => t.LastDisposition).HasMaxLength(64);
        builder.HasIndex(t => new { t.BotId, t.UpdateId }).IsUnique().HasFilter("update_id IS NOT NULL");
        builder.HasIndex(t => t.SourceMessageId);
        builder.HasIndex(t => new { t.CreatedAt, t.Id });
        builder.HasOne<StoredMessage>().WithMany().HasForeignKey(t => t.SourceMessageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
