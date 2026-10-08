using Assistant.Domain.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> b)
    {
        b.ToTable("reminders"); b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(32); b.Property(x => x.ChatType).HasMaxLength(16);
        b.Property(x => x.Text).HasMaxLength(500); b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.LastOutcome).HasMaxLength(16);
        b.HasIndex(x => new { x.BotId, x.ChatId, x.SourceMessageId }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.Status, x.DueAt });
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ChatId, x.TopicId, x.ActorUserId });
    }
}
public sealed class ReminderPreferenceConfiguration : IEntityTypeConfiguration<ReminderPreference>
{
    public void Configure(EntityTypeBuilder<ReminderPreference> b)
    {
        b.ToTable("reminder_preferences"); b.HasKey(x => new { x.FamilyId, x.ActorUserId });
    }
}
public sealed class ReminderAttemptConfiguration : IEntityTypeConfiguration<ReminderAttempt>
{
    public void Configure(EntityTypeBuilder<ReminderAttempt> b)
    {
        b.ToTable("reminder_attempts"); b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(32); b.Property(x => x.Outcome).HasMaxLength(16);
        b.HasOne<Reminder>().WithMany().HasForeignKey(x => x.ReminderId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ReminderId, x.OccurrenceDueAt }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.Role, x.StartedAt });
    }
}
public sealed class ReminderSettingsReceiptConfiguration : IEntityTypeConfiguration<ReminderSettingsReceipt>
{
    public void Configure(EntityTypeBuilder<ReminderSettingsReceipt> b)
    {
        b.ToTable("reminder_settings_receipts");
        b.HasKey(x => new { x.BotId, x.ChatId, x.SourceMessageId });
        b.HasIndex(x => new { x.FamilyId, x.CreatedAt });
    }
}
