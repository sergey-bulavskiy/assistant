using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

public sealed class VetPhotoRunWindowConfiguration : IEntityTypeConfiguration<VetPhotoRunWindow>
{
    public void Configure(EntityTypeBuilder<VetPhotoRunWindow> b)
    {
        b.ToTable("vet_photo_run_windows");
        VetPhotoConfiguration.Scope(b);
        b.HasIndex(x => new { x.RunId, x.Ordinal }).IsUnique();
        b.Property(x => x.State).HasMaxLength(24);
        b.Property(x => x.SelectionJson).HasMaxLength(65536);
        b.HasOne<VetPhotoRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Assistant.Domain.Vet.VetDiaryAction>().WithMany().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Restrict);
    }
}
