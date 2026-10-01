namespace Assistant.Application.Llm;

/// <summary>ContextStartMessageId is the messages.id of the most recent /new command in this
/// chat/topic, or null if none yet (spec §8.3) -- context only ever includes rows with a strictly
/// greater id.</summary>
public record ChatSettingInfo(string? PreferredModel, long? ContextStartMessageId);

/// <summary>Per (bot, chat, topic) General-assistant settings. botId has the same meaning as
/// messages.bot_id (the bot's Telegram id).</summary>
public interface IChatSettingsStore
{
    Task<ChatSettingInfo> GetAsync(long familyId, long botId, long chatId, int? topicId, CancellationToken cancellationToken);

    Task SetPreferredModelAsync(long familyId, long botId, long chatId, int? topicId, string? preferredModelOrNull, CancellationToken cancellationToken);

    /// <summary><paramref name="newCommandMessageId"/> is the /new command's OWN messages.id (the
    /// IMessageStore.StoreAsync StoreResult.MessageDbId for that command), not a timestamp (spec
    /// §8.3).</summary>
    Task SetContextStartMessageIdAsync(long familyId, long botId, long chatId, int? topicId, long newCommandMessageId, CancellationToken cancellationToken);
}
