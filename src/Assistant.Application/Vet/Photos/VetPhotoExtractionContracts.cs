using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoRunInputSnapshot(
    Guid SourceId, Guid InputRevisionId, Guid OriginalReferenceId, int ExpectedReferenceRevision,
    Guid ExpectedCurrentInputId, int ExpectedSourceOrdinal, int? ExpectedCandidateRevision, Guid AttemptKey)
{
    public string? AcknowledgedUnknownFingerprint { get; init; }
}
public enum VetPhotoImageStatus
{
    Claimed, Existing, Reused, Busy, Unknown, CapacityFull, NotFound, Refused, Stale,
    InvalidResult, Installed, ProposedDelta, EvidenceOnly
}
public enum VetPhotoImageFailureDisposition { KnownNotDispatched, OutcomeUnknown }
public sealed record VetPhotoImageClaim(
    VetDiaryScope Scope, Guid AttemptKey, Guid ClaimToken, DateTimeOffset LeaseUntil,
    long ActorUserId, Guid SourceId, Guid InputRevisionId, Guid ExpectedCurrentInputId,
    int ExpectedSourceOrdinal, bool HistoricalSelection, Guid? RunWindowId);
public sealed record VetPhotoCandidateDelta(
    Guid CandidateId, int ExpectedCandidateRevision, Guid SourceId, Guid InputRevisionId,
    Guid ExtractionResultId, bool RequiresExplicitRestoration);
public sealed record VetPhotoImageResult(
    VetPhotoImageStatus Status, VetPhotoImageClaim? Claim = null,
    VetPhotoExtraction? Extraction = null, VetPhotoCandidateDelta? Delta = null);
public sealed record VetPhotoImageCompletion(
    VetDiaryScope Scope, Guid AttemptKey, Guid ClaimToken, long ActorUserId,
    Guid SourceId, Guid InputRevisionId, string ModelName, string StructuredJson,
    Guid? DiagnosticAttemptId = null)
{
    public string PromptVersion { get; init; } = "photo-v1";
}

public interface IVetPhotoExtractionStore
{
    Task<VetPhotoImageResult> ClaimCurrentImageAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoImageResult> ClaimScheduledImageAsync(VetDiaryScope scope, Guid attemptKey,
        long actorUserId, CancellationToken cancellationToken);
    Task<bool> MarkImageDispatchedAsync(VetDiaryScope scope, Guid attemptKey,
        Guid claimToken, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoImageResult> CompleteImageAsync(VetPhotoImageCompletion completion,
        CancellationToken cancellationToken);
    Task<bool> RecordImageFailureAsync(VetDiaryScope scope, Guid attemptKey, Guid claimToken,
        long actorUserId, string category, VetPhotoImageFailureDisposition disposition,
        CancellationToken cancellationToken);
    Task<VetPhotoImageResult> InstallCurrentExtractionAsync(VetDiaryScope scope,
        Guid extractionResultId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoImageResult> ReuseDisplayAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, string modelName, CancellationToken cancellationToken, string promptVersion = "photo-v1");
}
