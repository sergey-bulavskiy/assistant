using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public sealed class HealthDocumentConfiguration : IEntityTypeConfiguration<HealthDocument>
{
    public void Configure(EntityTypeBuilder<HealthDocument> b)
    {
        b.ToTable("documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.TelegramFileId).IsRequired().HasMaxLength(1024);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.MimeType).HasMaxLength(255);
        b.Property(x => x.Caption).HasMaxLength(4096);
        b.Property(x => x.Text).HasMaxLength(200_000);
        b.Property(x => x.TextStatus).IsRequired().HasMaxLength(32);
        b.Property(x => x.TextFailureReason).HasMaxLength(40);
        b.HasIndex(x => new { x.FamilyId, x.ProfileId, x.SourceMessageId }).IsUnique();
        b.HasIndex(x => x.AdmissionId).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.ProfileId, x.DeletedAt, x.PostedAt, x.Id });
        b.HasOne<HealthProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StoredMessage>().WithMany().HasForeignKey(x => x.SourceMessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HealthDocumentAdmission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}
