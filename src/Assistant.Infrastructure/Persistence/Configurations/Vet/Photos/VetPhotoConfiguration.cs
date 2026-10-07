using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assistant.Infrastructure.Persistence.Configurations.Vet.Photos;

internal static class VetPhotoConfiguration
{
    public static void Scope<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("Id");
        b.HasIndex("FamilyId", "BotDbId", "TelegramBotId", "ChatId", "TopicId");
    }
}
