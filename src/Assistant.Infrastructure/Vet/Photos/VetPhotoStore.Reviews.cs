using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore
{
    private static readonly UTF8Encoding WorkflowUtf8 = new(false, true);
    private static string? ReviewKindName(VetPhotoReviewKind kind) => kind switch
    {
        VetPhotoReviewKind.Save => "save", VetPhotoReviewKind.Correction => "correction",
        VetPhotoReviewKind.Reverse => "reverse", VetPhotoReviewKind.ReextractSelection => "reextract_selection",
        VetPhotoReviewKind.ReextractComparison => "reextract_comparison", VetPhotoReviewKind.DeleteOriginals => "delete_originals",
        VetPhotoReviewKind.DeleteOriginalsSelection => "delete_originals_selection",
        VetPhotoReviewKind.Evidence => "evidence",
        _ => null
    };
    private static bool WorkflowTextBound(string text, int maxChars)
    {
        try { return text is not null && text.Length <= maxChars
            && WorkflowUtf8.GetByteCount(text) <= VetPhotoReviewBounds.MaxPostgresTextBytes; }
        catch (EncoderFallbackException) { return false; }
    }
    private bool SelectionBound(VetPhotoStageReview stage)
    {
        var max = stage.Kind is VetPhotoReviewKind.DeleteOriginalsSelection or VetPhotoReviewKind.ReextractSelection
            ? capacity.MaxInputRevisions : 50;
        if (!VetPhotoReviewBounds.TrySelectionChars(max, out var bound) || !WorkflowTextBound(stage.SelectionJson, bound)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stage.SelectionJson, new JsonDocumentOptions { MaxDepth = 16 });
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() is > 0
                && doc.RootElement.GetArrayLength() <= max
                && doc.RootElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object
                    && e.GetRawText().Length <= VetPhotoReviewBounds.MaxSelectionEntryChars);
        }
        catch (JsonException) { return false; }
    }

    private async Task<bool> ReviewCurrentAsync(VetDiaryScope scope, VetPhotoReview review, CancellationToken ct)
    {
        var profile = await WorkflowProfileAsync(scope, ct);
        if (profile is null || profile.Id != review.ProfileId || profile.Revision != review.ProfileRevision) return false;
        if (review.BatchId is not { } id) return true;
        var batch = await BatchAsync(scope, id, ct);
        return batch is not null && !(TerminalBatch(batch) && review.Kind == "save") && batch.ReviewRevision == review.BatchReviewRevision
            && profile.Id == batch.ProfileId && profile.Revision == batch.ProfileRevision;
    }

    public Task<VetPhotoReviewChange> StageReviewAsync(VetPhotoStageReview stage, CancellationToken ct) =>
        WorkflowAsync(stage.Scope, stage.RequesterUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var kind = ReviewKindName(stage.Kind);
            if (stage.OperationKey == Guid.Empty || kind is null || stage.Preview is null || stage.Preview.Pages is null || !stage.Preview.Success
                || stage.Preview.Pages.Count is < 1 or > VetPhotoReviewBounds.MaxPages || !SelectionBound(stage))
                return new(VetPhotoWorkflowStatus.Refused, null);
            if (stage.Kind is VetPhotoReviewKind.DeleteOriginals or VetPhotoReviewKind.DeleteOriginalsSelection
                && !await OwnerAsync(stage.Scope, stage.RequesterUserId, ct))
                return new(VetPhotoWorkflowStatus.Refused, null);
            if (stage.Preview.Pages.Any(p => string.IsNullOrWhiteSpace(p) || p.Contains('\0')
                || !WorkflowTextBound(p, VetPhotoReviewFormatter.MaxPageChars)))
                return new(VetPhotoWorkflowStatus.Refused, null);
            var pagesJson = JsonSerializer.Serialize(stage.Preview.Pages, Json);
            if (!WorkflowTextBound(pagesJson, VetPhotoReviewBounds.MaxPreviewJsonChars))
                return new(VetPhotoWorkflowStatus.Refused, null);
            var profile = await WorkflowProfileAsync(stage.Scope, ct);
            if (profile is null || profile.Revision != stage.ExpectedProfileRevision) return new(VetPhotoWorkflowStatus.Stale, null);
            if (stage.BatchId is { } batchId)
            {
                var batch = await BatchAsync(stage.Scope, batchId, ct);
                if (batch is null || TerminalBatch(batch) && stage.Kind == VetPhotoReviewKind.Save || batch.ProfileId != profile.Id || batch.ProfileRevision != profile.Revision
                    || batch.ReviewRevision != stage.ExpectedBatchRevision) return new(VetPhotoWorkflowStatus.Stale, null);
            }
            else if (stage.ExpectedBatchRevision != null) return new(VetPhotoWorkflowStatus.Refused, null);
            if (stage.Kind == VetPhotoReviewKind.Evidence && !await EvidenceSelectionCurrentAsync(stage, ct))
                return new(VetPhotoWorkflowStatus.Stale, null);
            if (stage.RunWindowId is { } window && !await Scoped<VetPhotoRunWindow>(stage.Scope).AnyAsync(w => w.Id == window, ct))
                return new(VetPhotoWorkflowStatus.Refused, null);
            var fingerprint = Hash(Encoding.UTF8.GetBytes(stage.SelectionJson));
            var prior = await Scoped<VetPhotoReview>(stage.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.OperationKey == stage.OperationKey, ct);
            if (prior is not null)
                return prior.Kind == kind && prior.BatchId == stage.BatchId && prior.RunWindowId == stage.RunWindowId
                    && prior.BatchReviewRevision == stage.ExpectedBatchRevision && prior.RequesterUserId == stage.RequesterUserId
                    && prior.Fingerprint == fingerprint && prior.SelectionJson == stage.SelectionJson && prior.PreviewPagesJson == pagesJson
                    ? new(VetPhotoWorkflowStatus.Existing, prior) : new(VetPhotoWorkflowStatus.Refused, null);
            var review = InScope(new VetPhotoReview { Id = Guid.NewGuid(), OperationKey = stage.OperationKey,
                Kind = kind, BatchId = stage.BatchId, RunWindowId = stage.RunWindowId, BatchReviewRevision = stage.ExpectedBatchRevision,
                ProfileId = profile.Id, ProfileRevision = profile.Revision,
                RequesterUserId = stage.RequesterUserId, SelectionJson = stage.SelectionJson, Fingerprint = fingerprint,
                PreviewPagesJson = pagesJson, PageCount = stage.Preview.Pages.Count, CreatedAt = clock.UtcNow }, stage.Scope);
            db.Add(review);
            return new(VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    private async Task<bool> EvidenceSelectionCurrentAsync(VetPhotoStageReview stage, CancellationToken ct)
    {
        if (stage.BatchId == null || stage.RunWindowId != null) return false;
        VetPhotoEvidenceSelection[]? selected;
        try { selected = JsonSerializer.Deserialize<VetPhotoEvidenceSelection[]>(stage.SelectionJson, Json); }
        catch (JsonException) { return false; }
        if (selected == null || selected.Length is < 1 or > 50
            || selected.Select(x => x.SourceId).Distinct().Count() != selected.Length) return false;
        var rows = await (from source in Scoped<VetPhotoSource>(stage.Scope).AsNoTracking()
                          join candidate in Scoped<VetPhotoCandidate>(stage.Scope).AsNoTracking() on source.Id equals candidate.SourceId
                          where source.BatchId == stage.BatchId && candidate.BatchId == stage.BatchId && candidate.CandidateOrdinal == 0
                          select new { source.Id, source.CurrentInputRevisionId, source.CurrentOrdinal,
                              CandidateId = candidate.Id, candidate.Revision, candidate.InputRevisionId, candidate.ExtractionResultId })
            .Take(51).ToListAsync(ct);
        if (rows.Count != selected.Length) return false;
        foreach (var item in selected)
        {
            var row = rows.SingleOrDefault(r => r.Id == item.SourceId);
            if (row == null || row.CurrentInputRevisionId != item.CurrentInputId || row.CurrentOrdinal != item.SourceOrdinal
                || row.CandidateId != item.CandidateId || row.Revision != item.CandidateRevision
                || row.InputRevisionId != item.CandidateInputId || row.ExtractionResultId != item.CandidateExtractionId) return false;
            if (item.ShownExtractionId is { } shown && !await Scoped<VetPhotoExtraction>(stage.Scope).AnyAsync(e =>
                e.Id == shown && e.SourceId == item.SourceId && e.InputRevisionId == item.CurrentInputId
                && (e.State == "returned" || e.State == "human" || e.State == "invalid" || e.State == "comparison"), ct)) return false;
        }
        return true;
    }

    private async Task<VetPhotoReview?> ExactPreviewAsync(VetPhotoReviewHandle handle, CancellationToken ct)
    {
        var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == handle.ReviewId, ct);
        if (review is null || review.OperationKey != handle.OperationKey || review.Revision != handle.Revision
            || review.State != "preview" || !await ReviewCurrentAsync(handle.Scope, review, ct)) return null;
        if (handle.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId) return null;
        return review;
    }

    // Failed state is a durable uncertain-send fence. Its claimed page is derived from immutable pages
    // and the first missing proof; explicit retry changes the revision before another send can begin.
    public Task<VetPhotoReviewChange> BeginPageDeliveryAsync(VetPhotoReviewHandle handle, int pageIndex,
        string textHash, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var review = await ExactPreviewAsync(handle, ct);
            if (review is null) return new(VetPhotoWorkflowStatus.Stale, null);
            var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
            var deliveries = JsonSerializer.Deserialize<List<VetPhotoPageDelivery>>(review.DeliveredPagesJson, Json)!;
            if (pages.Length != review.PageCount || pages.Length is < 1 or > VetPhotoReviewBounds.MaxPages
                || pageIndex < 0 || pageIndex >= pages.Length || textHash != Hash(Encoding.UTF8.GetBytes(pages[pageIndex]))
                || deliveries.Any(d => d.PageIndex < 0 || d.PageIndex >= pages.Length || d.MessageId <= 0
                    || d.TextHash != Hash(Encoding.UTF8.GetBytes(pages[d.PageIndex])))
                || deliveries.Select(d => d.PageIndex).Distinct().Count() != deliveries.Count
                || deliveries.Select(d => d.MessageId).Distinct().Count() != deliveries.Count)
                return new(VetPhotoWorkflowStatus.Refused, review);
            if (deliveries.Any(d => d.PageIndex == pageIndex)) return new(VetPhotoWorkflowStatus.Existing, review);
            var next = 0; while (next < pages.Length && deliveries.Any(d => d.PageIndex == next)) next++;
            if (review.CompletePreviewDelivered || pageIndex != next) return new(VetPhotoWorkflowStatus.Refused, review);
            db.Attach(review); review.State = "preview_failed";
            review.CompletePreviewDelivered = false; review.AcceptancePromptMessageId = null;
            return new(VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    private async Task<VetPhotoReview?> ExactPageDeliveryAsync(VetPhotoReviewHandle handle, CancellationToken ct)
    {
        var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == handle.ReviewId, ct);
        if (review is null || review.OperationKey != handle.OperationKey || review.Revision != handle.Revision
            || review.State is not ("preview" or "preview_failed") || !await ReviewCurrentAsync(handle.Scope, review, ct)) return null;
        if (handle.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId) return null;
        return review;
    }

    public Task<VetPhotoReviewChange> RecordPageDeliveryAsync(VetPhotoReviewHandle handle, int pageIndex,
        int messageId, string textHash, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var review = await ExactPageDeliveryAsync(handle, ct);
            if (review is null) return new(VetPhotoWorkflowStatus.Stale, null);
            var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
            if (pageIndex < 0 || pageIndex >= pages.Length || messageId <= 0
                || textHash != Hash(Encoding.UTF8.GetBytes(pages[pageIndex]))) return new(VetPhotoWorkflowStatus.Refused, review);
            var deliveries = JsonSerializer.Deserialize<List<VetPhotoPageDelivery>>(review.DeliveredPagesJson, Json)!;
            var prior = deliveries.SingleOrDefault(d => d.PageIndex == pageIndex);
            if (prior is not null)
                return new(prior.MessageId == messageId && prior.TextHash == textHash ? VetPhotoWorkflowStatus.Existing : VetPhotoWorkflowStatus.Refused, review);
            if (review.CompletePreviewDelivered || deliveries.Any(d => d.MessageId == messageId)) return new(VetPhotoWorkflowStatus.Refused, review);
            if (review.State == "preview_failed")
            {
                var next = 0; while (next < pages.Length && deliveries.Any(d => d.PageIndex == next)) next++;
                if (pageIndex != next) return new(VetPhotoWorkflowStatus.Refused, review);
            }
            var delivery = new VetPhotoPageDelivery(pageIndex, messageId, textHash);
            if (JsonSerializer.Serialize(delivery, Json).Length > VetPhotoReviewBounds.MaxDeliveryEntryChars)
                return new(VetPhotoWorkflowStatus.Refused, review);
            deliveries.Add(delivery);
            var serialized = JsonSerializer.Serialize(deliveries.OrderBy(d => d.PageIndex), Json);
            if (!WorkflowTextBound(serialized, VetPhotoReviewBounds.MaxDeliveryJsonChars)) return new(VetPhotoWorkflowStatus.Refused, review);
            db.Attach(review); review.DeliveredPagesJson = serialized; review.State = "preview";
            return new(VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    public Task<VetPhotoReviewChange> CompleteDeliveryAsync(VetPhotoReviewHandle handle, int lastPromptMessageId, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var review = await ExactPreviewAsync(handle, ct);
            if (review is null) return new(VetPhotoWorkflowStatus.Stale, null);
            var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
            if (lastPromptMessageId <= 0 || deliveries.Length != review.PageCount
                || deliveries.SingleOrDefault(d => d.PageIndex == review.PageCount - 1)?.MessageId != lastPromptMessageId)
                return new(VetPhotoWorkflowStatus.Incomplete, review);
            var already = review.CompletePreviewDelivered;
            review.AcceptancePromptMessageId = lastPromptMessageId; review.CompletePreviewDelivered = true;
            if (!ReviewDelivered(review)) return new(VetPhotoWorkflowStatus.Incomplete, review);
            db.Attach(review);
            // Attach after proof evaluation, then mark the two proof fields explicitly.
            db.Entry(review).Property(r => r.AcceptancePromptMessageId).IsModified = true;
            db.Entry(review).Property(r => r.CompletePreviewDelivered).IsModified = true;
            return new(already ? VetPhotoWorkflowStatus.Existing : VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    public Task<VetPhotoReviewChange> RecordPreviewFailureAsync(VetPhotoReviewHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var prior = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == handle.ReviewId, ct);
            if (prior is not null && prior.OperationKey == handle.OperationKey && prior.Revision == handle.Revision
                && prior.State == "preview_failed" && await ReviewCurrentAsync(handle.Scope, prior, ct))
                return new(VetPhotoWorkflowStatus.Existing, prior);
            var review = await ExactPreviewAsync(handle, ct);
            if (review is null) return new(VetPhotoWorkflowStatus.Stale, null);
            if (review.CompletePreviewDelivered) return new(VetPhotoWorkflowStatus.Existing, review);
            db.Attach(review); review.State = "preview_failed"; review.CompletePreviewDelivered = false; review.AcceptancePromptMessageId = null;
            return new(VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    public Task<VetPhotoReviewChange> RetryPreviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == handle.ReviewId, ct);
            if (review is null || review.OperationKey != handle.OperationKey || review.Revision != handle.Revision
                || review.State != "preview_failed" || !await ReviewCurrentAsync(handle.Scope, review, ct)) return new(VetPhotoWorkflowStatus.Stale, null);
            db.Attach(review); review.Revision++; review.State = "preview"; review.DeliveredPagesJson = "[]";
            review.CompletePreviewDelivered = false; review.AcceptancePromptMessageId = null;
            return new(VetPhotoWorkflowStatus.Applied, review);
        }, ct);

    public Task<VetPhotoReviewLookup> GetReviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoReviewLookup(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var review = await ExactPreviewAsync(handle, ct);
            if (review is null) return new(VetPhotoWorkflowStatus.Stale, null);
            if (!ReviewDelivered(review)) return new(VetPhotoWorkflowStatus.Incomplete, review);
            if (handle.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId)
                return new(VetPhotoWorkflowStatus.Stale, null);
            return new(VetPhotoWorkflowStatus.Existing, review);
        }, ct);

    public Task<VetPhotoReviewLookup> FindNaturalReviewAsync(VetDiaryScope scope, long actorUserId,
        Guid? operationKey, int? expectedRevision, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoReviewLookup(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var profile = await WorkflowProfileAsync(scope, ct);
            if (profile == null) return new(VetPhotoWorkflowStatus.NotFound, null);
            // Current authority is filtered in SQL before the two-row ambiguity bound.
            var scopedBatches = Scoped<VetPhotoBatch>(scope);
            var current = await Scoped<VetPhotoReview>(scope).AsNoTracking().Where(r => r.State == "preview"
                && r.ProfileId == profile.Id && r.ProfileRevision == profile.Revision
                && (operationKey == null || r.OperationKey == operationKey)
                && (expectedRevision == null || r.Revision == expectedRevision)
                && (r.Kind == "save" || r.Kind == "correction" || r.Kind == "reverse"
                    || r.Kind == "reextract_selection" || r.Kind == "reextract_comparison"
                    || r.Kind == "delete_originals_selection" || r.Kind == "delete_originals")
                && (r.BatchId == null || scopedBatches.Any(b => b.Id == r.BatchId
                    && b.ReviewRevision == r.BatchReviewRevision && b.ProfileId == profile.Id
                    && b.ProfileRevision == profile.Revision
                    && (r.Kind != "save" || b.State != "completed" && b.State != "cancelled"))))
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(2).ToListAsync(ct);
            if (current.Count > 1) return new(VetPhotoWorkflowStatus.Ambiguous, null);
            if (current.Count == 0) return new(VetPhotoWorkflowStatus.NotFound, null);
            return new(ReviewDelivered(current[0]) ? VetPhotoWorkflowStatus.Existing : VetPhotoWorkflowStatus.Incomplete, current[0]);
        }, ct);
}
