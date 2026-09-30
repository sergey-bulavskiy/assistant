namespace Assistant.Domain.Llm;

// One row per (bot, chat, topic) the General assistant has been configured for. TopicId is null
// for a chat-wide setting. PreferredModel null means "use the default tier chain" (/model auto).
// ContextStartMessageId null means "no /new yet in this chat/topic" -- context includes full
// history; otherwise it's the messages.id of the most recent /new command in this chat/topic, and
// context only ever includes rows with a strictly greater id (spec §8.3).
public class ChatSetting
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string? PreferredModel { get; set; }
    public long? ContextStartMessageId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
