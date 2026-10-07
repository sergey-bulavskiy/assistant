namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoDiaryAcceptance(VetDiaryScope Scope, Guid ReviewId, int ReviewRevision,
    Guid OperationKey, long ActorUserId, int? CallbackPromptMessageId = null);

public sealed record VetPhotoCollisionProof(string Fingerprint, bool HasCollisions);

public sealed record VetPhotoDiarySelection(long ProfileId, int ProfileRevision, Guid BatchId,
    int BatchReviewRevision, Guid CandidateId, int CandidateRevision, Guid SourceId,
    Guid ExpectedCurrentInputId, int ExpectedSourceOrdinal, Guid InputRevisionId,
    Guid? ExtractionResultId, Guid? ExpectedCandidateExtractionId,
    Guid? OriginalReferenceId, int? OriginalReferenceRevision, string? OriginalReferenceState,
    long? EventId, int? EventRevision, string Disposition, string DuplicateDecision,
    long? LinkEventId, int? LinkEventRevision, Guid? LinkCandidateId,
    bool ExplicitRestoration, VetPhotoContext Context, VetEventState? State,
    VetPhotoCollisionProof CollisionProof);

public sealed record VetPhotoCandidateState(string State, bool RequiresExplicitRestoration,
    bool ManuallyCorrected, string CorrectionProvenanceJson, string EffectiveJson,
    string ReasonsJson, string DuplicateDecision, Guid? DuplicateSourceId,
    long? DuplicateEventId, int? DuplicateEventRevision, long? EventId, int? EventRevision,
    Guid? InputRevisionId, Guid? ExtractionResultId, Guid? LastReviewId);

public sealed record VetPhotoActionCandidateChange(Guid CandidateId, Guid SourceId, int BeforeRevision,
    int AfterRevision, VetPhotoCandidateState Before, VetPhotoCandidateState After);

// Property names mirror VetMutationResult so its existing action replay/undo parser remains valid.
public sealed record VetPhotoActionOutcome(VetMutationStatus Status, long? ActionId,
    IReadOnlyList<long> EventIds, IReadOnlyList<long> ProtectedIds,
    IReadOnlyList<VetEventRevision> Revisions, IReadOnlyList<VetPhotoActionCandidateChange> PhotoChanges)
{
    public IReadOnlyList<Guid> ProtectedCandidateIds { get; init; } = [];
}

public interface IVetPhotoDiaryStore : IVetDiaryStore
{
    Task<VetPhotoCollisionProof?> GetPhotoCollisionProofAsync(VetDiaryScope scope,
        long profileId, Guid candidateId, VetEventState? state, CancellationToken cancellationToken);
    // Read-only composition hint. Acceptance independently rechecks exact closure and collision proof.
    Task<bool> CanAutoLinkPhotoGroupAsync(VetDiaryScope scope, long profileId,
        Guid canonicalCandidateId, IReadOnlyList<Guid> groupCandidateIds,
        VetEventState state, CancellationToken cancellationToken);
    Task<VetMutationResult> ApplyPhotoReviewAsync(VetPhotoDiaryAcceptance acceptance, CancellationToken cancellationToken);
}
