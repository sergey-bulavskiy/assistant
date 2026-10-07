using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

internal static class VetPhotoCaptionReader
{
    internal static async Task<VetPhotoCaptionEvidence?> ReadAsync(AssistantDbContext db, VetDiaryScope scope,
        VetPhotoSource source, VetPhotoInputRevision input, CancellationToken ct)
    {
        if (input.TextInputRevisionId is not { } textId)
            return input.Caption.Length == 0 ? null : new("failed", null, "caption_provenance_unavailable");
        var text = await db.Set<VetTextSourceRevision>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == textId
            && r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId && r.Text == input.Caption
            && db.Set<VetTextSource>().Any(s => s.Id == r.SourceId && s.FamilyId == scope.FamilyId && s.BotDbId == scope.BotDbId
                && s.TelegramBotId == scope.TelegramBotId && s.ChatId == scope.ChatId && s.TopicId == scope.TopicId
                && s.TelegramMessageId == source.TelegramMessageId && s.SourceAuthorUserId == source.SourceAuthorUserId
                && s.SourceMessageDbId == source.SourceMessageDbId), ct);
        if (text == null) return new("failed", null, "caption_provenance_unavailable");
        var result = await db.Set<VetExtractionResult>().AsNoTracking().SingleOrDefaultAsync(e =>
            e.InputRevisionId == text.Id && e.FamilyId == scope.FamilyId && e.BotDbId == scope.BotDbId, ct);
        return new(text.State, text.State is "written" or "completed" && result != null
            ? VetInterpretationParser.Parse(result.Json) : null, text.FailureCategory);
    }
}
