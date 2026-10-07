using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public enum VetPhotoRunPurpose { Reprocess, DeleteOriginals }
public enum VetPhotoRunSelectionMode { Current, AllOriginals, Selected }
public enum VetPhotoRunDateAxis { Measurement, Upload }
public sealed record VetPhotoRunSelectionRequest(VetDiaryScope Scope, Guid OperationKey, long ActorUserId,
    int ExpectedProfileRevision, VetPhotoRunPurpose Purpose, VetPhotoRunSelectionMode Mode,
    string ModelName, string ProviderName, Guid? BatchId = null, IReadOnlyList<Guid>? ReferenceIds = null,
    DateOnly? FromDate = null, DateOnly? UntilDate = null, VetPhotoRunDateAxis DateAxis = VetPhotoRunDateAxis.Measurement);
public sealed record VetPhotoRunChange(VetPhotoWorkflowStatus Status, VetPhotoRun? Run,
    VetPhotoReview? Review = null, VetPhotoRunWindow? Window = null, VetPhotoRunSelectionCounts? SelectionCounts = null);
public sealed record VetPhotoRunSelectionCounts(int SelectedSources, int SelectedInputs, int CurrentInputs,
    int SupersededInputs, int SupersededExcluded, int UnknownMeasurementSources, int DeletedByteSources,
    int UnknownAttempts = 0, int ChargedUnknownAttempts = 0, int LiveCalls = 0);
public sealed record VetPhotoRunHandle(VetDiaryScope Scope, Guid RunId, long ActorUserId);
public sealed record VetPhotoRunProgress(VetPhotoRun Run, int TotalWindows, int CompletedWindows,
    int RemainingInputs, IReadOnlyList<VetPhotoRunWindow> Windows, int? NextOffset);
public sealed record VetPhotoDeletionWindowResult(VetPhotoWorkflowStatus Status,
    VetPhotoOriginalDeletionResult? Deletion, VetPhotoRun? Run);

public interface IVetPhotoRunStore
{
    Task<VetPhotoRunChange> StageRunAsync(VetPhotoRunSelectionRequest request, CancellationToken cancellationToken);
    Task<VetPhotoRunChange> ApproveRunAsync(VetPhotoRunHandle handle, VetPhotoReviewHandle review, CancellationToken cancellationToken);
    Task<VetPhotoRunChange> ContinueRunAsync(VetPhotoRunHandle handle, CancellationToken cancellationToken);
    Task<VetPhotoRunChange> CancelRunAsync(VetPhotoRunHandle handle, CancellationToken cancellationToken);
    Task<VetPhotoRunProgress?> GetRunAsync(VetPhotoRunHandle handle, int offset, int limit, CancellationToken cancellationToken);
    Task<VetPhotoRunChange> AttachComparisonAsync(VetPhotoRunHandle handle, Guid windowId, Guid reviewId, CancellationToken cancellationToken);
    Task<VetPhotoRunChange> ReconcileWindowAsync(VetPhotoRunHandle handle, Guid windowId, CancellationToken cancellationToken);
    Task<VetPhotoDeletionWindowResult> ConfirmDeletionWindowAsync(VetPhotoRunHandle handle,
        Guid windowId, VetPhotoReviewHandle review, CancellationToken cancellationToken);
}
