using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoCaptionEvidence(string State, VetInterpretation? Interpretation,
    string? FailureCategory);
public sealed record VetPhotoPresentationEvidence(VetPhotoSource Source, VetPhotoInputRevision Input,
    VetPhotoCandidate Candidate, VetPhotoExtraction? Extraction, VetPhotoOriginalReference? Original,
    VetPhotoCaptionEvidence? Caption, VetEvent? OwnedEvent);
public sealed record VetPhotoValidationUpdate(VetDiaryScope Scope, Guid BatchId, int BatchRevision,
    Guid CandidateId, int CandidateRevision, Guid InputRevisionId, Guid ExtractionResultId,
    int ProfileRevision, long ActorUserId, VetPhotoContext Context, VetPhotoValidation Validation);
public sealed record VetPhotoHumanProposal(VetDiaryScope Scope, Guid BatchId, int BatchRevision,
    Guid CandidateId, int CandidateRevision, Guid InputRevisionId, int SourceOrdinal,
    long ActorUserId, VetPhotoContext Context, bool RestoreRequested = false);
public sealed record VetPhotoRecoverableBatch(VetDiaryScope Scope, Guid BatchId, long ActorUserId);

public interface IVetPhotoPresentationStore
{
    Task<VetPhotoPresentationEvidence?> ReadEvidenceAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, Guid? extractionResultId, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoBatchChange> RefreshProfileSnapshotAsync(VetDiaryScope scope, Guid batchId,
        int expectedBatchRevision, int profileRevision, long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoWorkflowStatus> SetValidationAsync(VetPhotoValidationUpdate update, CancellationToken cancellationToken);
    Task<VetPhotoWorkflowStatus> ProposeHumanCorrectionAsync(VetPhotoHumanProposal proposal, CancellationToken cancellationToken);
    Task<VetPhotoWorkflowStatus> SetProgressMessageAsync(VetDiaryScope scope, Guid batchId,
        long actorUserId, int messageId, CancellationToken cancellationToken);
    Task<VetPhotoReview?> ReadPreviewAsync(VetDiaryScope scope, Guid reviewId,
        long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoWorkflowStatus> DeclineReviewAsync(VetPhotoReviewHandle handle, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPhotoRecoverableBatch>> GetRecoverableBatchesAsync(long familyId,
        long botDbId, int limit, CancellationToken cancellationToken);
}

public static class VetPhotoCaptionContext
{
    public static (VetPhotoContext Context, string? Failure) Read(VetPhotoCaptionEvidence? evidence)
    {
        if (evidence == null) return (new(), null);
        if (evidence.State is not ("written" or "completed") || evidence.Interpretation == null)
            return (new(), evidence.State is "failed" or "paused" ? "caption_not_saved" : "caption_processing");
        if (evidence.Interpretation.PhotoCaption is { } caption)
            return caption.Intent == "record" ? (caption.Context, null) : (new(), "uncertain_caption_reading");
        var glucose = evidence.Interpretation.Events.Where(e => e.EventType == "glucose" && e.Intent != "question_only").ToArray();
        if (glucose.Length > 1) return (new(), "ambiguous_caption_readings");
        if (glucose.Length == 0) return (new(), null);
        var candidate = glucose[0];
        if (candidate.Intent != "record" || candidate.Unresolved != null) return (new(), "uncertain_caption_reading");
        int? year = null; int? month = null; int? day = null;
        if (candidate.Date != null)
        {
            if (!DateOnly.TryParseExact(candidate.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return (new(), "caption_date_requires_clarification");
            year = date.Year; month = date.Month; day = date.Day;
        }
        // Even an explicitly current caption must state the measurement clock/date for an image.
        return (new(candidate.RawValue, candidate.Unit, year, month, day, candidate.Time, candidate.Offset), null);
    }
}
