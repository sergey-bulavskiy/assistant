namespace Assistant.Domain.Health;

/// <summary>Values read from one message that wait for a family member's Да/Нет before they become
/// events (pending_records). Family-scoped; never deleted. Events is a jsonb array of validated
/// events, written and read only by PendingRecordStore. AlertedRuleKeys: rules whose fixed alert was
/// already sent while asking. BotId is the Telegram bot id (as events.bot_id); SourceMessageId is
/// messages.id of the original message; TelegramMessageId is that message's Telegram id and
/// PromptMessageId the bot's message with the buttons (null until it is sent).</summary>
public class PendingRecord
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long ProfileId { get; set; }
    public long? SourceMessageId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public int TelegramMessageId { get; set; }
    public int? PromptMessageId { get; set; }
    public long? RequestedByUserId { get; set; }
    public string Events { get; set; } = "[]";
    public string[] AlertedRuleKeys { get; set; } = Array.Empty<string>();

    /// <summary>One of <see cref="PendingRecordStatuses"/>.</summary>
    public string Status { get; set; } = PendingRecordStatuses.Pending;

    public long? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
