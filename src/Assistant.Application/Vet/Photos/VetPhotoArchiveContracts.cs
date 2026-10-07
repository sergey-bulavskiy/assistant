using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoAttachment(
    string FileId, string? FileUniqueId, string? FileName, string? ReportedMimeType,
    long? ReportedSize, int? ReportedWidth, int? ReportedHeight);
public sealed record VetPhotoBatchAssumptions(
    string? ProfileTimeZone, string? ProfileGlucoseUnit, string? TimeZone,
    string? GlucoseUnit, int? Year, bool TimeZoneConfirmed, bool UnitConfirmed, bool YearConfirmed);

public sealed record VetPhotoCapacity(
    long MaxBytes = 1_073_741_824, int MaxInputRevisions = 10_000, int MaxResults = 10_000)
{
    public bool IsValid => MaxBytes > 0 && MaxInputRevisions > 0 && MaxResults > 0;
}

public sealed record VetPhotoCapacityTotals(
    long RetainedBytes, long ReservedBytes, long RetainedInputs,
    long ReservedInputs, long RetainedResults, long ReservedResults)
{
    public bool CanReserve(VetPhotoCapacity capacity, long bytes, bool input, bool result) =>
        bytes >= 0 && bytes <= capacity.MaxBytes
        && RetainedBytes <= capacity.MaxBytes - bytes
        && ReservedBytes <= capacity.MaxBytes - bytes - RetainedBytes
        && (!input || RetainedInputs < capacity.MaxInputRevisions
            && ReservedInputs < capacity.MaxInputRevisions - RetainedInputs)
        && (!result || RetainedResults < capacity.MaxResults
            && ReservedResults < capacity.MaxResults - RetainedResults);
}

public enum VetPhotoAdmissionStatus { Admitted, Existing, Late, Full, Refused, InvalidMetadata }
public sealed record VetPhotoAdmission(
    VetPhotoAdmissionStatus Status, VetPhotoSource? Source, VetPhotoInputRevision? Input);

public enum VetPhotoArchiveStatus
{
    Reserved, Existing, Retained, Stale, Refused, CapacityFull,
    OriginalDeleted, NotFound, InvalidImage
}
public sealed record VetPhotoDownloadClaim(
    VetDiaryScope Scope, VetPhotoSource Source, VetPhotoInputRevision Input,
    VetPhotoAttempt Attempt, Guid ClaimToken);
public sealed record VetPhotoReservation(
    VetPhotoArchiveStatus Status, VetPhotoDownloadClaim? Claim, VetPhotoOriginalReference? Reference);

public sealed record VetPhotoArchiveCommit(
    VetDiaryScope Scope, long ActorUserId, Guid AttemptId, Guid ClaimToken,
    Guid InputRevisionId, ReadOnlyMemory<byte> Original, VetPhotoImageInfo Image);
public sealed record VetPhotoArchiveResult(
    VetPhotoArchiveStatus Status, VetPhotoOriginalReference? Reference);

public sealed record VetPhotoOriginalRead(
    Guid ReaderLeaseId, Guid InputRevisionId, Guid OriginalReferenceId,
    int OriginalReferenceRevision, byte[] Original, VetPhotoImageInfo Image);

public sealed record VetPhotoOriginalSelection(
    Guid ReferenceId, int Revision, Guid InputRevisionId, Guid BlobId,
    Guid SourceId, Guid ExpectedCurrentInputId, int ExpectedSourceOrdinal,
    long ExpectedBlobRetainedReferences, long? EventId, int? EventRevision);
public sealed record VetPhotoPageDelivery(int PageIndex, int MessageId, string TextHash);
public sealed record VetPhotoOriginalDeletion(
    VetDiaryScope Scope, Guid ReviewId, int ReviewRevision, Guid OperationKey,
    long ActorUserId, int? CallbackPromptMessageId);
public sealed record VetPhotoOriginalDeletionResult(
    VetMutationStatus Status, int ReferenceCount, long ReclaimableBytes);

public interface IVetPhotoArchiveStore
{
    Task<VetPhotoAdmission> AdmitAsync(
        VetDiaryScope scope, IncomingMessage message, long updateId,
        VetPhotoAttachment attachment, Guid? textInputRevisionId, CancellationToken cancellationToken);
    Task<bool> BindMessageAsync(
        VetDiaryScope scope, Guid sourceId, long sourceMessageDbId, CancellationToken cancellationToken);
    Task<VetPhotoAdmission?> FindSourceAsync(
        VetDiaryScope scope, int telegramMessageId, CancellationToken cancellationToken);
    Task<VetPhotoAdmission?> GetSourceAsync(
        VetDiaryScope scope, Guid sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPhotoAdmission>> GetUnboundAsync(
        long familyId, long botDbId, int limit, CancellationToken cancellationToken);
    Task<VetPhotoReservation> ReserveDownloadAsync(
        VetDiaryScope scope, Guid sourceId, Guid inputRevisionId, long actorUserId,
        CancellationToken cancellationToken);
    Task<VetPhotoArchiveResult> CommitOriginalAsync(
        VetPhotoArchiveCommit commit, CancellationToken cancellationToken);
    Task<bool> RecordDownloadFailureAsync(
        VetDiaryScope scope, Guid attemptId, Guid claimToken, string category,
        bool knownTransient, CancellationToken cancellationToken);
    Task<VetPhotoOriginalRead?> ReadOriginalAsync(
        VetDiaryScope scope, Guid inputRevisionId, long actorUserId,
        Guid attemptId, Guid claimToken, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken);
    Task ReleaseReaderAsync(
        VetDiaryScope scope, Guid readerLeaseId, CancellationToken cancellationToken);
    Task<VetPhotoCapacityTotals> GetCapacityAsync(
        VetDiaryScope scope, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoOriginalDeletionResult> DeleteOriginalsAsync(
        VetPhotoOriginalDeletion deletion, CancellationToken cancellationToken);
    Task<int> ReclaimAsync(
        long familyId, int limit, CancellationToken cancellationToken);
}
