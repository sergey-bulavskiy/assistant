using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Reminders;

public sealed record ReminderScope(long FamilyId, long BotDbId, long BotId, string Role,
    long ChatId, int? TopicId, string ChatType, long ActorUserId);
public sealed record ReminderPreferences(int OffsetMinutes = 0, int QuietStartMinute = 1320, int QuietEndMinute = 480);
public sealed record ReminderRequest(string Text, DateTimeOffset DueAt, int? DailyMinute, int OffsetMinutes);
public sealed record ReminderParseResult(bool Recognized, string Kind, ReminderRequest? Request = null,
    ReminderPreferences? Preferences = null, string? Error = null);
public sealed record ReminderItem(Guid Id, string Text, string Status, DateTimeOffset DueAt,
    int? DailyMinute, int OffsetMinutes, DateTimeOffset CreatedAt, string? LastOutcome = null,
    DateTimeOffset? LastAttemptAt = null);
public sealed record ReminderIntake(ReminderScope Scope, string Kind, Guid? Id = null, string? Error = null);
public sealed record ReminderDispatch(Guid ReminderId, Guid AttemptId, long ChatId, int? TopicId, string Text);
public interface IReminderStore
{
    Task<ReminderPreferences> GetPreferencesAsync(ReminderScope scope, CancellationToken ct);
    Task SetPreferencesAsync(ReminderScope scope, int sourceMessageId, ReminderPreferences preferences, CancellationToken ct);
    Task<ReminderItem?> AdmitAsync(ReminderScope scope, int sourceMessageId, ReminderRequest request, CancellationToken ct);
    Task<IReadOnlyList<ReminderItem>> ListAsync(ReminderScope scope, CancellationToken ct);
    Task<ReminderItem?> BeginPreviewAsync(ReminderScope scope, Guid id, CancellationToken ct);
    Task BindPreviewAsync(ReminderScope scope, Guid id, int messageId, CancellationToken ct);
    Task<string> SaveAsync(ReminderScope scope, Guid id, int previewMessageId, CancellationToken ct);
    Task<string> CancelAsync(ReminderScope scope, Guid id, CancellationToken ct);
    Task<ReminderDispatch?> ClaimDueAsync(ReceivingBot bot, CancellationToken ct);
    Task CompleteAsync(ReceivingBot bot, ReminderDispatch dispatch, int telegramMessageId, CancellationToken ct);
    Task CleanupAsync(ReceivingBot bot, CancellationToken ct);
}
public interface IReminderAssistant
{
    Task<ReminderIntake?> AdmitAsync(ReceivingBot bot, IncomingMessage message, bool replyToAll, CancellationToken ct);
    Task HandleAsync(ReminderIntake intake, ITelegramClient client, IncomingMessage message, StoreResult stored, CancellationToken ct);
    Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct);
    Task TickAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct);
}
