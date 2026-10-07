using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

public sealed partial class VetDiaryStore
{
    private sealed record PhotoComparisonAuthority(VetPhotoRunWindow Window, VetPhotoRun Run,
        VetPhotoRunInputSnapshot[] Selected);

    private async Task<PhotoComparisonAuthority?> LoadPhotoComparisonAuthorityAsync(VetDiaryScope scope,
        VetPhotoReview review, Dictionary<Guid, PhotoComparisonAuthority> cache, CancellationToken ct)
    {
        if (review.RunWindowId is not { } windowId) return null;
        if (cache.TryGetValue(windowId, out var cached)) return cached;
        var window = await PhotoRows<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w => w.Id == windowId, ct);
        if (window == null) return null;
        var run = await PhotoRows<VetPhotoRun>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == window.RunId, ct);
        if (run == null || run.SelectionMode.StartsWith("deletion_", StringComparison.Ordinal)) return null;
        var approval = await PhotoRows<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.SelectionReviewId, ct);
        if (approval == null || approval.State != "accepted" || approval.Kind != "reextract_selection"
            || approval.DecisionActorUserId != run.ActorUserId || approval.ProfileId != review.ProfileId
            || approval.SelectionJson != run.SelectionJson || !VetPhotoStore.HasCompletePreview(approval)
            || !string.Equals(Hash(run.SelectionJson), approval.Fingerprint, StringComparison.OrdinalIgnoreCase)) return null;
        var all = ReadPhotoComparisonManifest(run.SelectionJson, 10_000);
        var selected = ReadPhotoComparisonManifest(window.SelectionJson, 50);
        if (all == null || selected == null || all.Length != run.SelectedCount) return null;
        var full = all.ToDictionary(s => s.AttemptKey);
        if (selected.Any(s => !full.TryGetValue(s.AttemptKey, out var approved) || approved != s)) return null;
        var authority = new PhotoComparisonAuthority(window, run, selected);
        cache.Add(windowId, authority);
        return authority;
    }

    private static VetPhotoRunInputSnapshot[]? ReadPhotoComparisonManifest(string text, int max)
    {
        if (!VetPhotoReviewBounds.TryRunSelectionChars(max, out var bound) || text.Length > bound) return null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1
                || document.RootElement.GetArrayLength() > max || document.RootElement.EnumerateArray().Any(e =>
                    e.ValueKind != JsonValueKind.Object || e.GetRawText().Length > VetPhotoReviewBounds.MaxRunSelectionEntryChars)) return null;
            var entries = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(text, PhotoJson);
            return entries != null && entries.All(s => s != null && s.SourceId != Guid.Empty && s.InputRevisionId != Guid.Empty
                && s.OriginalReferenceId != Guid.Empty && s.AttemptKey != Guid.Empty && s.ExpectedReferenceRevision > 0
                && s.ExpectedCurrentInputId != Guid.Empty && s.ExpectedSourceOrdinal > 0)
                && entries.Select(s => s.InputRevisionId).Distinct().Count() == entries.Length
                && entries.Select(s => s.AttemptKey).Distinct().Count() == entries.Length ? entries : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<bool> PhotoComparisonProofAsync(VetDiaryScope scope, VetPhotoReview review,
        VetPhotoDiarySelection item, VetPhotoSource source, VetPhotoInputRevision input,
        VetPhotoExtraction? extraction, Dictionary<Guid, PhotoComparisonAuthority> cache, CancellationToken ct)
    {
        var authority = await LoadPhotoComparisonAuthorityAsync(scope, review, cache, ct);
        if (authority == null) return false;
        var window = authority.Window; var run = authority.Run;
        var live = review.Kind == "reextract_comparison" && window.ComparisonReviewId == review.Id
            && window.State == "awaiting_review" && run.State == "running" && run.CancelledAt == null
            && run.NextWindowOrdinal == window.Ordinal;
        var late = review.Kind == "correction" && (window.State is "completed" or "cancelled")
            && (run.State is "running" or "completed" or "cancelled");
        var held = review.Kind == "correction" && run.State == "stale" && run.CancelledAt == null;
        if (!live && !late && !held) return false;
        var snapshot = authority.Selected.SingleOrDefault(s => s.SourceId == source.Id && s.InputRevisionId == input.Id);
        if (snapshot == null || item.OriginalReferenceId != snapshot.OriginalReferenceId) return false;
        if ((live || held) && (source.CurrentInputRevisionId != snapshot.ExpectedCurrentInputId
            || source.CurrentOrdinal != snapshot.ExpectedSourceOrdinal
            || item.OriginalReferenceRevision != snapshot.ExpectedReferenceRevision)) return false;
        var attempt = await PhotoRows<VetPhotoAttempt>(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == snapshot.AttemptKey, ct);
        if (attempt == null || attempt.RunWindowId != window.Id || attempt.Kind != "image"
            || attempt.SourceId != source.Id || attempt.InputRevisionId != input.Id || attempt.ActorUserId != run.ActorUserId
            || attempt.ExpectedCurrentInputId != snapshot.ExpectedCurrentInputId
            || attempt.ExpectedSourceOrdinal != snapshot.ExpectedSourceOrdinal) return false;
        if (extraction == null)
            return live && item.Disposition == "keep" && item.ExtractionResultId == null
                && attempt.ExtractionResultId == null && (attempt.State == "failed" && !attempt.ReservedResultSlot
                    || attempt.State == "unknown" && attempt.ReservedResultSlot);
        return (attempt.State == "returned" && extraction.State == "comparison"
                || live && item.Disposition == "keep" && attempt.State == "failed" && extraction.State == "invalid")
            && !attempt.ReservedResultSlot && attempt.ExtractionResultId == extraction.Id
            && extraction.AttemptId == attempt.Id && extraction.SourceId == source.Id && extraction.InputRevisionId == input.Id
            ;
    }
}
