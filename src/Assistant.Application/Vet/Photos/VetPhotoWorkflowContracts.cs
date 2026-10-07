using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public enum VetPhotoWorkflowStatus { Applied, Existing, Refused, Stale, NotFound, Full, Ambiguous, Incomplete }
public static class VetPhotoReviewBounds
{
    public const int MaxPages = VetPhotoReviewFormatter.MaxReviewPages;
    public const int MaxSelectionEntryChars = 4096;
    public const int MaxRunSelectionEntryChars = 768;
    public const int MaxPreviewJsonChars = MaxPages * (3500 * 6 + 3) + 1;
    public const int MaxDeliveryEntryChars = 192;
    public const int MaxDeliveryJsonChars = MaxPages * (MaxDeliveryEntryChars + 1) + 1;
    public const int MaxPostgresTextBytes = 1_073_741_823;
    public static bool TrySelectionChars(int maxEntries, out int chars)
    {
        var count = (long)maxEntries * (MaxSelectionEntryChars + 1) + 1;
        chars = count is > 0 and <= MaxPostgresTextBytes ? (int)count : 0;
        return maxEntries > 0 && chars != 0;
    }
    public static bool TryRunSelectionChars(int maxEntries, out int chars)
    {
        var count = (long)maxEntries * (MaxRunSelectionEntryChars + 1) + 1;
        chars = count is > 0 and <= MaxPostgresTextBytes ? (int)count : 0;
        return maxEntries > 0 && chars != 0;
    }
}
public sealed record VetPhotoBatchChange(VetPhotoWorkflowStatus Status, VetPhotoBatch? Batch);
public sealed record VetPhotoBatchCounts(int Admitted, int Delivered, int Retained, int Waiting,
    int Processed, int Clear, int Pending, int Failed, int Saved, int Excluded, int Cancelled, int Rejected);
public sealed record VetPhotoBatchItem(VetPhotoSource Source, VetPhotoInputRevision Input, VetPhotoCandidate Candidate);
public sealed record VetPhotoBatchSnapshot(VetPhotoBatch Batch, VetPhotoBatchCounts Counts, IReadOnlyList<VetPhotoBatchItem> Items);
public sealed record VetPhotoBatchSummary(VetPhotoBatch Batch, VetPhotoBatchCounts Counts);
public sealed record VetPhotoBatchHistory(IReadOnlyList<VetPhotoBatchSummary> Batches, int? NextOffset);
public enum VetPhotoAssumptionKind { Year, TimeZone, Unit }
public sealed record VetPhotoAssumptionChange(VetDiaryScope Scope, Guid BatchId, int ExpectedBatchRevision,
    int ExpectedProfileRevision, long ActorUserId, VetPhotoAssumptionKind Kind, string Value);
public enum VetPhotoCandidateChangeKind { Correct, Exclude, Restore, DuplicateExisting, DuplicateSeparate }
public sealed record VetPhotoCandidateChange(VetDiaryScope Scope, Guid BatchId, Guid CandidateId,
    int ExpectedBatchRevision, int ExpectedCandidateRevision, Guid ExpectedCurrentInputId,
    int ExpectedSourceOrdinal, Guid? ExpectedExtractionId, long ActorUserId,
    VetPhotoCandidateChangeKind Kind, VetPhotoEffectiveReading? Reading = null,
    Guid? DuplicateSourceId = null, long? DuplicateEventId = null, int? DuplicateEventRevision = null);
public enum VetPhotoReviewKind { Save, Correction, Reverse, ReextractSelection, ReextractComparison, DeleteOriginalsSelection, DeleteOriginals, Evidence }
public sealed record VetPhotoStageReview(VetDiaryScope Scope, Guid OperationKey, long RequesterUserId,
    VetPhotoReviewKind Kind, Guid? BatchId, int? ExpectedBatchRevision, int ExpectedProfileRevision,
    string SelectionJson, VetPhotoPreviewResult Preview, Guid? RunWindowId = null);
public sealed record VetPhotoReviewChange(VetPhotoWorkflowStatus Status, VetPhotoReview? Review);
public sealed record VetPhotoReviewHandle(VetDiaryScope Scope, Guid ReviewId, int Revision,
    Guid OperationKey, long ActorUserId, int? CallbackPromptMessageId = null);
public sealed record VetPhotoReviewLookup(VetPhotoWorkflowStatus Status, VetPhotoReview? Review);

public interface IVetPhotoWorkflowStore
{
    Task<VetPhotoBatchChange> StartCollectionAsync(VetDiaryScope scope, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoBatchChange> CloseCollectionAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoBatchSnapshot?> GetBatchAsync(VetDiaryScope scope, Guid batchId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoBatchHistory> ListBatchesAsync(VetDiaryScope scope, long actorUserId, int offset, int limit, CancellationToken cancellationToken);
    Task<VetPhotoBatchChange> CancelRemainderAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoBatchChange> ChangeAssumptionAsync(VetPhotoAssumptionChange change, CancellationToken cancellationToken);
    Task<VetPhotoWorkflowStatus> ChangeCandidateAsync(VetPhotoCandidateChange change, CancellationToken cancellationToken);
    Task<VetPhotoBatchChange> AddLateSourceAsync(VetDiaryScope scope, Guid batchId, int expectedRevision,
        Guid sourceId, Guid expectedCurrentInputId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> StageReviewAsync(VetPhotoStageReview stage, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> BeginPageDeliveryAsync(VetPhotoReviewHandle handle, int pageIndex,
        string textHash, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> RecordPageDeliveryAsync(VetPhotoReviewHandle handle, int pageIndex,
        int messageId, string textHash, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> CompleteDeliveryAsync(VetPhotoReviewHandle handle, int lastPromptMessageId, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> RecordPreviewFailureAsync(VetPhotoReviewHandle handle, CancellationToken cancellationToken);
    Task<VetPhotoReviewChange> RetryPreviewAsync(VetPhotoReviewHandle handle, CancellationToken cancellationToken);
    Task<VetPhotoReviewLookup> GetReviewAsync(VetPhotoReviewHandle handle, CancellationToken cancellationToken);
    Task<VetPhotoReviewLookup> FindNaturalReviewAsync(VetDiaryScope scope, long actorUserId,
        Guid? operationKey, int? expectedRevision, CancellationToken cancellationToken);
}

public sealed record VetPhotoEvidenceSelection(Guid SourceId, Guid CurrentInputId, int SourceOrdinal,
    Guid CandidateId, int CandidateRevision, Guid? CandidateInputId, Guid? CandidateExtractionId, Guid? ShownExtractionId);
