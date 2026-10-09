using Assistant.Domain.Expectations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class ExpectationConfiguration : IEntityTypeConfiguration<Expectation>
{
    public void Configure(EntityTypeBuilder<Expectation> b)
    {
        b.ToTable("expectations"); b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(32); b.Property(x => x.ChatType).HasMaxLength(16);
        b.Property(x => x.EventType).HasMaxLength(32); b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.LastOutcome).HasMaxLength(32);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.BotId, x.Role, x.ChatId, x.TopicId,
            x.ProfileId, x.EventType }).IsUnique().AreNullsDistinct(false)
            .HasFilter("status IN ('draft', 'active', 'paused')");
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.Status, x.DueAt });
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ChatId, x.TopicId, x.ActorUserId });
    }
}
public sealed class ExpectationVersionConfiguration : IEntityTypeConfiguration<ExpectationVersion>
{
    public void Configure(EntityTypeBuilder<ExpectationVersion> b)
    {
        b.ToTable("expectation_versions"); b.HasKey(x => new { x.ExpectationId, x.Number });
        b.HasOne<Expectation>().WithMany().HasForeignKey(x => x.ExpectationId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ExpectationDraftConfiguration : IEntityTypeConfiguration<ExpectationDraft>
{
    public void Configure(EntityTypeBuilder<ExpectationDraft> b)
    {
        b.ToTable("expectation_drafts"); b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(16); b.Property(x => x.Status).HasMaxLength(16);
        b.HasOne<Expectation>().WithMany().HasForeignKey(x => x.ExpectationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ExpectationId).IsUnique().HasFilter("status = 'pending'");
        b.HasIndex(x => new { x.FamilyId, x.UpdatedAt });
    }
}
public sealed class ExpectationOccurrenceConfiguration : IEntityTypeConfiguration<ExpectationOccurrence>
{
    public void Configure(EntityTypeBuilder<ExpectationOccurrence> b)
    {
        b.ToTable("expectation_occurrences"); b.HasKey(x => new { x.ExpectationId, x.LocalDate });
        b.Property(x => x.Outcome).HasMaxLength(32); b.Property(x => x.MatchedEventKind).HasMaxLength(16);
        b.HasOne<Expectation>().WithMany().HasForeignKey(x => x.ExpectationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ExpectationVersion>().WithMany().HasForeignKey(x => new { x.ExpectationId, x.Version })
            .HasPrincipalKey(x => new { x.ExpectationId, x.Number }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FamilyId, x.UpdatedAt });
    }
}
public sealed class ExpectationAttemptConfiguration : IEntityTypeConfiguration<ExpectationAttempt>
{
    public void Configure(EntityTypeBuilder<ExpectationAttempt> b)
    {
        b.ToTable("expectation_attempts"); b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(32); b.Property(x => x.Outcome).HasMaxLength(16);
        b.HasIndex(x => new { x.ExpectationId, x.LocalDate }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.Role, x.StartedAt });
        // Typed identity stays durable until bounded child-first cleanup; no cascading budget loss.
        b.HasOne<ExpectationOccurrence>().WithMany().HasForeignKey(x => new { x.ExpectationId, x.LocalDate })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ExpectationReceiptConfiguration : IEntityTypeConfiguration<ExpectationReceipt>
{
    public void Configure(EntityTypeBuilder<ExpectationReceipt> b)
    {
        b.ToTable("expectation_receipts"); b.HasKey(x => new { x.BotId, x.ChatId, x.SourceMessageId });
        b.Property(x => x.Result).HasMaxLength(32);
        b.HasIndex(x => new { x.FamilyId, x.CreatedAt });
    }
}
