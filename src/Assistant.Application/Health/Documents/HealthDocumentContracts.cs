using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Health.Documents;

public sealed record HealthDocumentScope(long FamilyId, long ProfileId, long BotDbId, long TelegramBotId)
{
    public static HealthDocumentScope From(ReceivingBot bot, long profileId) =>
        new(bot.FamilyId!.Value, profileId, bot.BotDbId, bot.TelegramBotId);
}

public sealed record HealthDocumentAdmissionInfo(
    Guid Id, HealthDocumentScope Scope, long ChatId, int? TopicId, string ChatType,
    int TelegramMessageId, long? SenderUserId, DateTimeOffset SentAt,
    DocumentAttachment Attachment, string? Caption, long? SourceMessageId,
    string Status, int AttemptCount, DateTimeOffset? NextAttemptAt,
    bool ReactionAttempted, bool NoticeAttempted);

public sealed record HealthDocumentInfo(
    long Id, long SourceMessageId, DateTimeOffset PostedAt, string? FileName,
    string? Caption, string TextStatus, string? FailureReason, bool TextTruncated,
    string AdmissionStatus, DateTimeOffset? NextAttemptAt, string? Text = null, bool ContextTextTruncated = false);

public sealed record HealthDocumentLease(
    Guid LeaseId, HealthDocumentAdmissionInfo Admission, HealthDocumentInfo Document);

public sealed record HealthDocumentContextSnapshot(IReadOnlyList<HealthDocumentInfo> Documents)
{
    public static HealthDocumentContextSnapshot Empty { get; } = new([]);
}

public sealed record HealthDocumentSourceDeletion(
    bool DocumentDeleted, DeletedEvents DeletedEvents, IReadOnlyList<PendingRecordInfo> ClosedPending)
{
    public bool Changed => DocumentDeleted || DeletedEvents.Events.Count > 0 || ClosedPending.Count > 0;
    public static HealthDocumentSourceDeletion None { get; } = new(false, DeletedEvents.None, []);
}

public static class HealthDocumentLimits
{
    public const long MaxBytes = 20_000_000;
    public const int MaxTextCharacters = 200_000;
    public const int ContextCharacters = 20_000;
    public const int MaxAttempts = 3;
    public const int RecoveryBatchSize = 10;
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(60);
    public static TimeSpan RetryDelay(int attempt) => attempt == 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);
}
