using Assistant.Domain.Bots;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class HealthDocumentAdmissionConfiguration : IEntityTypeConfiguration<HealthDocumentAdmission>
{
    public void Configure(EntityTypeBuilder<HealthDocumentAdmission> b)
    {
        b.ToTable("health_document_admissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.ChatType).IsRequired().HasMaxLength(16);
        b.Property(x => x.FileId).IsRequired().HasMaxLength(1024);
        b.Property(x => x.FileUniqueId).HasMaxLength(1024);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.MimeType).HasMaxLength(255);
        b.Property(x => x.Caption).HasMaxLength(4096);
        b.Property(x => x.Status).IsRequired().HasMaxLength(32);
        b.Property(x => x.FailureReason).HasMaxLength(40);
        b.HasIndex(x => new { x.FamilyId, x.TelegramBotId, x.ChatId, x.TelegramMessageId }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.Status, x.NextAttemptAt, x.LeaseExpiresAt });
        b.HasOne<Bot>().WithMany().HasForeignKey(x => x.BotDbId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HealthProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StoredMessage>().WithMany().HasForeignKey(x => x.SourceMessageId).OnDelete(DeleteBehavior.Restrict);
    }
}
