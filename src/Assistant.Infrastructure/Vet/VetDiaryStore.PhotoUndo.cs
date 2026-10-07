using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

public sealed partial class VetDiaryStore
{
    private sealed record PhotoCandidateInverse(VetPhotoCandidate Candidate, VetPhotoCandidateState Before,
        VetPhotoCandidateState After, int Revision, bool Created);
    private sealed record PhotoUndoPreparation(IReadOnlyList<PhotoCandidateInverse> Inverses,
        HashSet<long> ProtectedEventIds, IReadOnlyList<Guid> ProtectedCandidateIds);

    private async Task<PhotoUndoPreparation> PreparePhotoUndoAsync(VetDiaryScope scope,
        VetDiaryAction target, IReadOnlyList<VetDiaryActionChange> eventChanges, CancellationToken ct)
    {
        VetPhotoActionOutcome? outcome;
        try { outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>(target.OutcomeJson); }
        catch (JsonException) { outcome = null; }
        if (outcome?.PhotoChanges == null) return new([], [], []);
        var inverses = new List<PhotoCandidateInverse>();
        var protectedEvents = new HashSet<long>();
        var protectedCandidates = new List<Guid>();
        foreach (var change in outcome.PhotoChanges)
        {
            var candidate = await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == change.CandidateId && c.SourceId == change.SourceId, ct);
            var ownedEvent = change.After.EventId;
            if (candidate == null || candidate.Revision != change.AfterRevision || PhotoCandidateState(candidate) != change.After)
            {
                protectedCandidates.Add(change.CandidateId);
                if (ownedEvent is { } protectedId) protectedEvents.Add(protectedId);
                continue;
            }
            var eventId = ownedEvent ?? change.After.DuplicateEventId;
            var expectedRevision = ownedEvent == null ? change.After.DuplicateEventRevision : change.After.EventRevision;
            if (eventId is { } id && !await Events(scope).AnyAsync(e => e.Id == id && e.Revision == expectedRevision, ct))
            {
                protectedCandidates.Add(change.CandidateId);
                if (ownedEvent != null) protectedEvents.Add(id);
                continue;
            }
            var created = ownedEvent is { } createdId && eventChanges.Any(e => e.EventId == createdId && e.BeforeJson == null)
                || change.Before.EventId == null && change.Before.DuplicateEventId == null && change.After.DuplicateEventId != null;
            var inverse = created ? change.After with { State = "deleted", RequiresExplicitRestoration = true,
                LastReviewId = null } : change.Before with { LastReviewId = null };
            inverses.Add(new(candidate, PhotoCandidateState(candidate), inverse, candidate.Revision, created));
        }
        // A protected canonical fact also protects its otherwise unchanged linked dispositions.
        var safe = new List<PhotoCandidateInverse>();
        foreach (var inverse in inverses)
        {
            if (inverse.Before.EventId is { } owned && protectedEvents.Contains(owned)
                || inverse.Before.DuplicateEventId is { } linked && protectedEvents.Contains(linked))
            {
                protectedCandidates.Add(inverse.Candidate.Id);
                continue;
            }
            safe.Add(inverse);
        }
        return new(safe, protectedEvents, protectedCandidates.Distinct().ToArray());
    }

    private async Task<IReadOnlyList<VetPhotoActionCandidateChange>> ApplyPhotoUndoAsync(VetDiaryScope scope,
        PhotoUndoPreparation prepared, IReadOnlyDictionary<long, int> newEventRevisions, CancellationToken ct)
    {
        var changes = new List<VetPhotoActionCandidateChange>();
        var batches = new HashSet<Guid>();
        foreach (var inverse in prepared.Inverses)
        {
            var candidate = inverse.Candidate;
            db.Attach(candidate);
            ApplyPhotoCandidateState(candidate, inverse.After);
            if (candidate.EventId is { } own && newEventRevisions.TryGetValue(own, out var ownRevision))
                candidate.EventRevision = ownRevision;
            if (candidate.DuplicateEventId is { } linked && newEventRevisions.TryGetValue(linked, out var linkedRevision))
                candidate.DuplicateEventRevision = linkedRevision;
            candidate.Revision = checked(inverse.Revision + 1);
            candidate.UpdatedAt = clock.UtcNow;
            if (candidate.BatchId is { } batchId) batches.Add(batchId);
            changes.Add(new(candidate.Id, candidate.SourceId, inverse.Revision, candidate.Revision,
                inverse.Before, PhotoCandidateState(candidate)));
        }
        foreach (var batch in batches)
        {
            await PhotoRows<VetPhotoBatch>(scope).Where(b => b.Id == batch).ExecuteUpdateAsync(u => u
                .SetProperty(b => b.ReviewRevision, b => b.ReviewRevision + 1).SetProperty(b => b.UpdatedAt, clock.UtcNow), ct);
            await PhotoRows<VetPhotoReview>(scope).Where(r => r.BatchId == batch && r.State == "preview").ExecuteUpdateAsync(u => u
                .SetProperty(r => r.State, "stale").SetProperty(r => r.CompletePreviewDelivered, false)
                .SetProperty(r => r.AcceptancePromptMessageId, (int?)null), ct);
        }
        return changes;
    }

    private static void ApplyPhotoCandidateState(VetPhotoCandidate c, VetPhotoCandidateState s)
    {
        c.State = s.State; c.RequiresExplicitRestoration = s.RequiresExplicitRestoration;
        c.ManuallyCorrected = s.ManuallyCorrected; c.CorrectionProvenanceJson = s.CorrectionProvenanceJson;
        c.EffectiveJson = s.EffectiveJson; c.ReasonsJson = s.ReasonsJson; c.DuplicateDecision = s.DuplicateDecision;
        c.DuplicateSourceId = s.DuplicateSourceId; c.DuplicateEventId = s.DuplicateEventId;
        c.DuplicateEventRevision = s.DuplicateEventRevision; c.EventId = s.EventId; c.EventRevision = s.EventRevision;
        c.InputRevisionId = s.InputRevisionId; c.ExtractionResultId = s.ExtractionResultId; c.LastReviewId = s.LastReviewId;
    }
}
