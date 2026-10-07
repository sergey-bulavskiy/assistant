using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet;

public sealed class VetDiaryActionChangeConfiguration : IEntityTypeConfiguration<VetDiaryActionChange>
{
    public void Configure(EntityTypeBuilder<VetDiaryActionChange> b)
    {
        b.ToTable("vet_diary_action_changes");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ActionId, x.EventId }).IsUnique();

        b.Property(x => x.BeforeJson).HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnType("jsonb");
        b.HasOne<VetDiaryAction>().WithMany().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VetEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
