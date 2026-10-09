using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;

namespace Assistant.Application.Expectations;

public sealed record ExpectationIntake(ReminderScope Scope, string Kind,
    Guid? Id = null, Guid? DraftId = null, string? Result = null);
public sealed record ExpectationPreview(Guid Id, Guid DraftId, string Subject,
    string EventType, int DeadlineMinute, int GraceMinutes, int OffsetMinutes,
    DateOnly EffectiveFrom, ReminderPreferences Preferences);
public sealed record ExpectationItem(Guid Id, string Subject, string EventType,
    string Status, int DeadlineMinute, int GraceMinutes, int OffsetMinutes,
    DateOnly? EffectiveFrom, DateOnly? LastDate, string? LastOutcome,
    DateOnly? SkippedFrom, DateOnly? SkippedThrough);

public interface IExpectationStore
{
    Task<ExpectationIntake> ExecuteAsync(ReminderScope scope, int sourceMessageId,
        ExpectationCommand command, CancellationToken ct);
    Task<ExpectationPreview?> BeginPreviewAsync(ReminderScope scope, Guid draftId, CancellationToken ct);
    Task BindPreviewAsync(ReminderScope scope, Guid draftId, int messageId, CancellationToken ct);
    Task<string> ResolveAsync(ReminderScope scope, Guid draftId, int messageId, bool save, CancellationToken ct);
    Task<IReadOnlyList<ExpectationItem>> ListAsync(ReminderScope scope, CancellationToken ct);
}

public interface IExpectationAssistant
{
    Task<ExpectationIntake?> AdmitAsync(ReceivingBot bot, IncomingMessage message, CancellationToken ct);
    Task HandleAsync(ExpectationIntake intake, ITelegramClient client, IncomingMessage message,
        StoreResult stored, CancellationToken ct);
    Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback,
        CancellationToken ct);
}

public sealed record NonurgentCandidate(string Kind, Guid Id, DateTimeOffset DueAt);
public sealed record NonurgentDispatch(string Kind, Guid Id, Guid AttemptId,
    long ChatId, int? TopicId, string Text);

public interface INonurgentDispatchStore
{
    Task CleanupAsync(ReceivingBot bot, CancellationToken ct);
    Task<IReadOnlyList<NonurgentCandidate>> SelectAsync(ReceivingBot bot, CancellationToken ct);
    Task<NonurgentDispatch?> ClaimAsync(ReceivingBot bot, NonurgentCandidate candidate, CancellationToken ct);
    Task CompleteAsync(ReceivingBot bot, NonurgentDispatch dispatch, int messageId, CancellationToken ct);
}
