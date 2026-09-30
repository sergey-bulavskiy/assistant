using Assistant.Application.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeChatSettingsStore : IChatSettingsStore
{
    private readonly Dictionary<(long BotId, long ChatId, int? TopicId), (string? Model, long? ContextStartMessageId)> _rows = new();

    public Task<ChatSettingInfo> GetAsync(long familyId, long botId, long chatId, int? topicId, CancellationToken cancellationToken)
    {
        var row = _rows.GetValueOrDefault((botId, chatId, topicId));
        return Task.FromResult(new ChatSettingInfo(row.Model, row.ContextStartMessageId));
    }

    public Task SetPreferredModelAsync(long familyId, long botId, long chatId, int? topicId, string? preferredModelOrNull, CancellationToken cancellationToken)
    {
        var key = (botId, chatId, topicId);
        var existing = _rows.GetValueOrDefault(key);
        _rows[key] = (preferredModelOrNull, existing.ContextStartMessageId);
        return Task.CompletedTask;
    }

    public Task SetContextStartMessageIdAsync(long familyId, long botId, long chatId, int? topicId, long newCommandMessageId, CancellationToken cancellationToken)
    {
        var key = (botId, chatId, topicId);
        var existing = _rows.GetValueOrDefault(key);
        _rows[key] = (existing.Model, newCommandMessageId);
        return Task.CompletedTask;
    }
}
