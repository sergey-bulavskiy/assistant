using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Llm;

/// <summary>/tokens. Family-scoped (query filter plus an explicit FamilyId predicate). Only Ok
/// attempts count, so a fallback's LimitReached/Failed attempts never double count.</summary>
public class LlmUsageQuery : ILlmUsageQuery
{
    private readonly AssistantDbContext _db;

    public LlmUsageQuery(AssistantDbContext db)
    {
        _db = db;
    }

    public async Task<LlmUsageSummary> GetChatUsageAsync(
        long familyId, long botId, long chatId, int? topicId, long? afterMessageId, CancellationToken cancellationToken)
    {
        // c.TopicId == topicId with a nullable parameter is translated null-safely by EF Core.
        var query = _db.LlmCalls.AsNoTracking()
            .Where(c => c.FamilyId == familyId && c.BotId == botId && c.ChatId == chatId && c.TopicId == topicId
                        && c.Outcome == LlmCallOutcome.Ok && c.TriggerMessageId != null);
        if (afterMessageId is { } cutoff)
        {
            query = query.Where(c => c.TriggerMessageId > cutoff);
        }

        var perModel = await query
            .GroupBy(c => c.Model)
            .Select(g => new
            {
                Model = g.Key,
                Calls = g.Count(),
                InputTokens = g.Sum(c => (long)(c.InputTokens ?? 0)),
                OutputTokens = g.Sum(c => (long)(c.OutputTokens ?? 0))
            })
            .ToListAsync(cancellationToken);

        return new LlmUsageSummary(perModel
            .OrderBy(m => m.Model, StringComparer.Ordinal)
            .Select(m => new LlmModelUsage(m.Model, m.Calls, m.InputTokens, m.OutputTokens))
            .ToArray());
    }
}
