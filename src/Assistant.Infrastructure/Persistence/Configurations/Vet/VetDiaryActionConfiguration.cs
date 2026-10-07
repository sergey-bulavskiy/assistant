using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetDiaryActionConfiguration : IEntityTypeConfiguration<VetDiaryAction>
{
    public void Configure(EntityTypeBuilder<VetDiaryAction> b)
    {
        b.ToTable("vet_diary_actions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.OperationKey }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ChatId, x.TopicId, x.ActorUserId, x.CreatedAt, x.Id });
        b.Property(x => x.Kind).HasMaxLength(30);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.OutcomeJson).HasColumnType("jsonb");

    }
}
