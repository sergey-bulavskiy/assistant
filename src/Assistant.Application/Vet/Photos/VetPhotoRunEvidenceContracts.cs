using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoRunEvidenceItem(VetPhotoRunInputSnapshot Snapshot,
    VetPhotoAttempt? Attempt, VetPhotoPresentationEvidence Evidence);
public sealed record VetPhotoRunEvidence(VetPhotoRun Run, VetPhotoRunWindow Window,
    VetPhotoReview InitialReview, IReadOnlyList<VetPhotoRunEvidenceItem> Items);
public sealed record VetPhotoRecoverableRun(VetDiaryScope Scope, Guid RunId, long ActorUserId);
public sealed record VetPhotoRunReviewOwner(Guid RunId, Guid? WindowId);

public interface IVetPhotoRunEvidenceStore
{
    Task<VetPhotoRunEvidence?> ReadWindowAsync(VetPhotoRunHandle handle, Guid windowId, CancellationToken cancellationToken);
    Task<VetPhotoRunReviewOwner?> FindReviewRunAsync(VetDiaryScope scope, Guid reviewId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoRecoverableRun?> FindAttemptRunAsync(VetDiaryScope scope, Guid attemptId, long actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>?> ResolveCurrentReferencesAsync(VetDiaryScope scope, IReadOnlyList<Guid> sourceIds, long actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPhotoRecoverableRun>> GetRecoverableRunsAsync(long familyId, long botDbId, int limit, CancellationToken cancellationToken);
}

public interface IVetPhotoRunReviewSelectionStore
{
    Task<VetPhotoRunChange> ReplaceComparisonAsync(VetPhotoRunHandle handle, Guid windowId,
        Guid expectedPriorReviewId, int expectedPriorRevision, Guid newReviewId, CancellationToken cancellationToken);
}
