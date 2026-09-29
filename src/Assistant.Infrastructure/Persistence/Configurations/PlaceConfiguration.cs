using Assistant.Domain.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations;

public class PlaceConfiguration : IEntityTypeConfiguration<Place>
{
    public void Configure(EntityTypeBuilder<Place> builder)
    {
        builder.ToTable("places");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Title).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        // AreNullsDistinct(false) (Postgres NULLS NOT DISTINCT, PG15+): plain chats and DMs always
        // have a null TopicId, which is the common case, not the exception — without this,
        // Postgres's default "each NULL is distinct" semantics would let two concurrent inserts for
        // the same (BotId, ChatId) with topic_id null both succeed, silently defeating the unique
        // constraint this index exists for.
        builder.HasIndex(p => new { p.BotId, p.ChatId, p.TopicId }).IsUnique().AreNullsDistinct(false);
    }
}
