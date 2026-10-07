using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;

namespace Assistant.Application.Vet;

public sealed record VetDiaryScope(long FamilyId, long BotDbId, long TelegramBotId, long ChatId, int? TopicId)
{
    public static VetDiaryScope From(ReceivingBot bot, IncomingMessage message) =>
        new(bot.FamilyId!.Value, bot.BotDbId, bot.TelegramBotId, message.ChatId, message.TopicId);
}

public sealed record VetEventState(
    string EventType, decimal Value, string Unit, string? Product,
    DateTimeOffset OccurredAt, string LocalTime, string TimeZoneSnapshot,
    string OccurredAtSource, string ValueUnitSource, string SourceKind, Guid SourceId,
    int CandidateOrdinal, Guid? TextSourceId, Guid? PhotoSourceId, Guid? PhotoBatchId,
    Guid InputRevisionId, Guid ExtractionResultId, long SourceAuthorUserId,
    long SourceMessageDbId, int TelegramMessageId, DateTimeOffset? DeletedAt = null,
    string? DeleteReason = null, long? DeletedByUserId = null);

public sealed record VetEventChange(long? EventId, int? ExpectedRevision, VetEventState State);
public sealed record VetDiaryMutation(
    VetDiaryScope Scope, Guid OperationKey, long ActorUserId, string Kind,
    long ProfileId, IReadOnlyList<VetEventChange> Changes,
    Guid? SourceId = null, Guid? InputRevisionId = null,
    long? PendingDecisionId = null, int? ReviewRevision = null);
public enum VetMutationStatus { Applied, AlreadyApplied, Stale, NotFound, Refused, NoChange }
public sealed record VetMutationResult(VetMutationStatus Status, long? ActionId,
    IReadOnlyList<long> EventIds, IReadOnlyList<long> ProtectedIds)
{
    public IReadOnlyList<VetEventRevision> Revisions { get; init; } = [];
    public IReadOnlyList<Guid> ProtectedCandidateIds { get; init; } = [];
    public static VetMutationResult Of(VetMutationStatus status) => new(status, null, [], []);
}
public sealed record VetEventRevision(long EventId, int Revision);
public sealed record VetAdmittedSource(VetTextSource Source, VetTextSourceRevision Revision);
public sealed record VetHistoryPage(IReadOnlyList<VetHistoryFact> Facts, int Total, int Offset, bool HasMore);
public sealed record VetHistoryFact(long? EventId, string EventType, decimal Value, string Unit,
    string? Product, DateTimeOffset OccurredAt, int Revision, bool Corrected);
public sealed record VetProfileChange(string Field, string? Value);
public sealed record VetProfileResult(bool Applied, bool Stale, VetProfile Profile);

public interface IVetProfileStore
{
    Task<VetProfile> GetOrCreateAsync(long familyId, long botDbId, CancellationToken cancellationToken);
    Task<VetProfileResult> UpdateAsync(long familyId, long botDbId, long actorUserId,
        int expectedRevision, IReadOnlyList<VetProfileChange> changes, CancellationToken cancellationToken);
}

public interface IVetDiaryStore
{
    Task<VetAdmittedSource> AdmitAsync(VetDiaryScope scope, IncomingMessage message, long updateId, CancellationToken cancellationToken);
    Task<VetAdmittedSource?> GetSourceAsync(VetDiaryScope scope, Guid sourceId, CancellationToken cancellationToken);
    Task<VetAdmittedSource?> FindSourceAsync(VetDiaryScope scope, int telegramMessageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetAdmittedSource>> GetResumableAsync(long familyId, long botDbId, int limit, CancellationToken cancellationToken);
    Task LinkMessageAsync(VetDiaryScope scope, Guid sourceId, long messageDbId, CancellationToken cancellationToken);
    Task<bool> SetProcessingAsync(VetDiaryScope scope, Guid revisionId, string expectedState, string state,
        string? failureCategory, CancellationToken cancellationToken);
    Task<bool> RetryAsync(VetDiaryScope scope, Guid sourceId, CancellationToken cancellationToken);
    Task<VetExtractionResult?> GetResultAsync(VetDiaryScope scope, Guid revisionId, CancellationToken cancellationToken);
    Task<bool> SaveResultAsync(VetDiaryScope scope, Guid revisionId, string json, string modelName,
        Guid? attemptId, CancellationToken cancellationToken);
    Task SetAnswerAsync(VetDiaryScope scope, Guid revisionId, string state, string? text, CancellationToken cancellationToken);
    Task SaveWorkAsync(VetDiaryScope scope, Guid revisionId, string json, CancellationToken cancellationToken);
    Task SetHistoryAsync(VetDiaryScope scope, Guid revisionId, string json, CancellationToken cancellationToken);
    Task<string?> GetLastHistoryAsync(VetDiaryScope scope, Guid exceptRevisionId, CancellationToken cancellationToken);
    Task<bool> GetReplyToAllAsync(VetDiaryScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetEvent>> GetSourceEventsAsync(VetDiaryScope scope, Guid sourceId, CancellationToken cancellationToken);
    Task<VetEvent?> GetEventAsync(VetDiaryScope scope, long eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetEvent>> FindDateTypeAsync(VetDiaryScope scope, string eventType,
        DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
    Task<VetHistoryPage> QueryAsync(VetDiaryScope scope, long profileId, DateTimeOffset from,
        DateTimeOffset until, int offset, int pageSize, CancellationToken cancellationToken);
    Task<VetPendingDecision> PutPendingAsync(VetDiaryScope scope, Guid sourceId, Guid revisionId,
        Guid resultId, long requester, string proposalJson, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPendingDecision>> GetPendingAsync(VetDiaryScope scope, CancellationToken cancellationToken);
    Task<VetPendingDecision?> GetPendingAsync(VetDiaryScope scope, long id, CancellationToken cancellationToken);
    Task<bool> RevisePendingAsync(VetDiaryScope scope, long id, int expectedRevision,
        string proposalJson, CancellationToken cancellationToken);
    Task SetPromptAsync(VetDiaryScope scope, long id, int reviewRevision, int messageId, CancellationToken cancellationToken);
    Task<bool> DeclineAsync(VetDiaryScope scope, long id, int reviewRevision, long actorUserId, CancellationToken cancellationToken);
    Task<VetMutationResult> ApplyAsync(VetDiaryMutation mutation, CancellationToken cancellationToken);
    Task<VetMutationResult> UndoAsync(VetDiaryScope scope, long actorUserId, Guid operationKey, CancellationToken cancellationToken);
}

public interface IVetAssistant
{
    Task<VetAdmittedSource?> AdmitAsync(ReceivingBot bot, IncomingMessage message, long updateId, CancellationToken cancellationToken);
    Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        StoreResult stored, VetAdmittedSource? admitted, CancellationToken cancellationToken, bool replyToAll = false);
    Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken);
    Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken cancellationToken);
}
