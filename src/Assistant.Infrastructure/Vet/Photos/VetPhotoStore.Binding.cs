using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Messages;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoBindingStore
{
    public async Task<bool> BindStoredMessageAsync(VetDiaryScope scope, Guid sourceId, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await ImageActorAsync(scope, actorUserId, ct)) return false;
        var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId && s.SourceSlot == 1, ct);
        if (source == null) return false;
        var messageId = await db.Messages.AsNoTracking().Where(m => m.FamilyId == scope.FamilyId
            && m.BotId == scope.TelegramBotId && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId
            && m.TelegramMessageId == source.TelegramMessageId && m.UserId == source.SourceAuthorUserId
            && m.Direction == MessageDirection.In && m.ChatType == source.ChatType && m.SentAt == source.SentAt
            && (m.Kind == MessageKind.Photo || m.Kind == MessageKind.Document))
            .Select(m => (long?)m.Id).SingleOrDefaultAsync(ct);
        return messageId is { } id && await BindMessageAsync(scope, source.Id, id, ct);
    }
}
