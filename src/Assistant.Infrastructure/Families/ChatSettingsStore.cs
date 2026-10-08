using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Families;

public class ChatSettingsStore : IChatSettingsStore
{
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;

    public ChatSettingsStore(AssistantDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ChatSettingInfo> GetAsync(long familyId, long botId, long chatId, int? topicId, CancellationToken cancellationToken)
    {
        var row = await _db.ChatSettings.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BotId == botId && c.ChatId == chatId && c.TopicId == topicId, cancellationToken);
        return new ChatSettingInfo(row?.PreferredModel, row?.ContextStartMessageId);
    }

    public Task SetPreferredModelAsync(long familyId, long botId, long chatId, int? topicId, string? preferredModelOrNull, CancellationToken cancellationToken) =>
        UpsertAsync(familyId, botId, chatId, topicId, row => row.PreferredModel = preferredModelOrNull, cancellationToken);

    public async Task SetContextStartMessageIdAsync(long familyId, long botId, long chatId, int? topicId, long newCommandMessageId, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({botId})", cancellationToken);
        await UpsertAsync(familyId, botId, chatId, topicId, row => row.ContextStartMessageId = newCommandMessageId, cancellationToken);
        await _db.GeneralMemoryStates.Where(x => x.FamilyId == familyId && x.BotId == botId && x.ChatId == chatId && x.TopicId == topicId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResetCutoff, newCommandMessageId)
                .SetProperty(x => x.ThroughMessageId, newCommandMessageId)
                .SetProperty(x => x.SummaryText, "").SetProperty(x => x.SourceFingerprint, "")
                .SetProperty(x => x.SourceVersion, x => x.SourceVersion + 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Read-then-write: two concurrent first writes for the same (bot, chat, topic) can race and one
    // fails on the unique index. Updates for one bot are handled sequentially by its poller, so this
    // does not happen in practice.
    private async Task UpsertAsync(long familyId, long botId, long chatId, int? topicId, Action<ChatSetting> mutate, CancellationToken cancellationToken)
    {
        var row = await _db.ChatSettings.FirstOrDefaultAsync(c => c.BotId == botId && c.ChatId == chatId && c.TopicId == topicId, cancellationToken);
        if (row is null)
        {
            row = new ChatSetting { FamilyId = familyId, BotId = botId, ChatId = chatId, TopicId = topicId };
            _db.ChatSettings.Add(row);
        }

        mutate(row);
        row.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
