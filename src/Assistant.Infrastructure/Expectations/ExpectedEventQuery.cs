using Assistant.Domain.Expectations;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

internal sealed class ExpectedEventQuery(AssistantDbContext db)
{
    public async Task<string?> SubjectAsync(Expectation e, CancellationToken ct) => e.Role switch
    {
        "health" => await db.HealthProfiles.AsNoTracking().Where(p => p.FamilyId == e.FamilyId
            && p.Id == e.ProfileId && p.BotId == e.BotDbId).Select(p => p.SubjectTag).SingleOrDefaultAsync(ct),
        "vet" => await db.Set<VetProfile>().AsNoTracking().Where(p => p.FamilyId == e.FamilyId
            && p.Id == e.ProfileId && p.BotDbId == e.BotDbId && p.Name != null && p.Name != "")
            .Select(p => p.Name).SingleOrDefaultAsync(ct),
        _ => null
    };

    public Task<long?> MatchAsync(Expectation e, ExpectationOccurrence o, CancellationToken ct)
    {
        if (e.Role == "health") return db.Events.AsNoTracking().Where(x =>
            x.FamilyId == e.FamilyId && x.ProfileId == e.ProfileId && x.BotId == e.BotId
            && x.ChatId == e.ChatId && x.TopicId == e.TopicId && x.Type == e.EventType
            && x.DeletedAt == null && x.OccurredAt >= o.WindowStart && x.OccurredAt <= o.DueAt)
            .OrderBy(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);
        if (e.Role == "vet") return db.Set<VetEvent>().AsNoTracking().Where(x =>
            x.FamilyId == e.FamilyId && x.ProfileId == e.ProfileId && x.BotDbId == e.BotDbId
            && x.TelegramBotId == e.BotId && x.ChatId == e.ChatId && x.TopicId == e.TopicId
            && x.EventType == e.EventType && x.DeletedAt == null
            && x.OccurredAt >= o.WindowStart && x.OccurredAt <= o.DueAt)
            .OrderBy(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);
        throw new InvalidOperationException("Expected-event role unavailable.");
    }
}
