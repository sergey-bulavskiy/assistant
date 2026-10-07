using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetPendingDecisionConfiguration : IEntityTypeConfiguration<VetPendingDecision>
{
    public void Configure(EntityTypeBuilder<VetPendingDecision> b)
    {
        b.ToTable("vet_pending_decisions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.FamilyId, x.BotDbId, x.ChatId, x.TopicId, x.State });
        b.HasIndex(x => x.InputRevisionId).IsUnique();
        b.Property(x => x.State).HasMaxLength(30);
        b.Property(x => x.ProposalJson).HasColumnType("jsonb");

    }
}
