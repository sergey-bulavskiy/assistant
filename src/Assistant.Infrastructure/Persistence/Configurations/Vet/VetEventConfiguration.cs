using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetEventConfiguration : IEntityTypeConfiguration<VetEvent>
{
    public void Configure(EntityTypeBuilder<VetEvent> b)
    {
        b.ToTable("vet_events");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.SourceKind, x.SourceId, x.EventType, x.CandidateOrdinal }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ProfileId, x.OccurredAt, x.Id });
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ChatId, x.TopicId, x.SourceId });
        b.Property(x => x.EventType).HasMaxLength(20);
        b.Property(x => x.Unit).HasMaxLength(20);
        b.Property(x => x.Product).HasMaxLength(100);
        b.Property(x => x.LocalTime).HasMaxLength(40);
        b.Property(x => x.TimeZoneSnapshot).HasMaxLength(100);
        b.Property(x => x.OccurredAtSource).HasMaxLength(30);
        b.Property(x => x.ValueUnitSource).HasMaxLength(30);
        b.Property(x => x.SourceKind).HasMaxLength(10);
        b.Property(x => x.DeleteReason).HasMaxLength(40);
        b.Property(x => x.LastMutationKind).HasMaxLength(30);

        b.Property(x => x.Value).HasColumnType("numeric");
        b.HasOne<VetProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
