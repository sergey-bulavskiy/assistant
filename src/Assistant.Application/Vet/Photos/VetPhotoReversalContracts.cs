using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoReversalEntry(long ActionId, string ActionFingerprint, long ProfileId,
    int ProfileRevision, Guid CandidateId, Guid SourceId, int CandidateRevision,
    Guid CurrentInputId, int SourceOrdinal, long? EventId, int? EventRevision,
    long? LinkEventId, int? LinkEventRevision, string HistoryHash, string SourceHash,
    string CandidateHash, string EventHash, string LinkHash, bool CandidateProtected, bool EventProtected);
public sealed record VetPhotoReversalItem(VetPhotoReversalEntry Entry, long SourceAuthorUserId,
    Guid? InputRevisionId, Guid? ExtractionResultId, VetPhotoCandidateState OriginalBefore,
    VetPhotoCandidateState OriginalAfter, VetPhotoCandidateState CurrentCandidate,
    VetPhotoCandidateState? InverseCandidate, VetEventState? OriginalEventBefore,
    VetEventState? OriginalEventAfter, VetEventState? CurrentEvent, VetEventState? CurrentLinkedEvent, VetEventState? InverseEvent);
public sealed record VetPhotoReversalPreview(long ActionId, long OriginalActorUserId,
    DateTimeOffset OriginalActionAt, long ProfileId, int ProfileRevision,
    IReadOnlyList<VetPhotoReversalItem> Items);
public interface IVetPhotoReversalStore
{
    Task<VetPhotoReversalPreview?> ReadReversalAsync(VetDiaryScope scope, long actionId,
        IReadOnlyList<Guid> candidateIds, long actorUserId, CancellationToken cancellationToken);
    Task<VetMutationResult> ApplyReversalAsync(VetPhotoDiaryAcceptance acceptance,
        CancellationToken cancellationToken);
}
