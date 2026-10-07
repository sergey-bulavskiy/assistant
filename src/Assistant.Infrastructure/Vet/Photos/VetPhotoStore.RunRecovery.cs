using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore
{
    private const int MaxRunRepairsPerPoll = 16;
    private async Task<bool> AcceptedRunMarkerAsync(VetDiaryScope scope, VetPhotoRun run, CancellationToken ct)
    {
        var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w => w.RunId == run.Id && w.Ordinal == run.NextWindowOrdinal, ct);
        if (window?.ComparisonReviewId is not { } id || window.State != "awaiting_review") return false;
        var review = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.RunWindowId == window.Id, ct);
        if (review == null || review.State != "accepted" || !ReviewDelivered(review) || review.DecisionActorUserId is not > 0) return false;
        if (DeletionRun(run)) return false; // Deletion confirmation advances its window in the archive-owned flow.
        return review.Kind == "reextract_comparison" && review.ActionId is { } action && await Scoped<VetDiaryAction>(scope)
            .AnyAsync(a => a.Id == action && a.ActorUserId == review.DecisionActorUserId, ct);
    }
    private async Task<bool> RunRecoveryCurrentAsync(VetDiaryScope scope, VetPhotoRun run, CancellationToken ct)
    {
        if (run.State is "stale" or "cancelled" or "completed") return true;
        if (await AcceptedRunMarkerAsync(scope, run, ct)) return true;
        var profile = await WorkflowProfileAsync(scope, ct);
        var review = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.SelectionReviewId, ct);
        if (profile == null || review == null || review.ProfileId != profile.Id || review.ProfileRevision != profile.Revision
            || review.Kind != (DeletionRun(run) ? "delete_originals_selection" : "reextract_selection")
            || review.SelectionJson != run.SelectionJson
            || !string.Equals(review.Fingerprint, Hash(Encoding.UTF8.GetBytes(run.SelectionJson)), StringComparison.OrdinalIgnoreCase)) return false;
        if (run.State == "preview")
        {
            var valid = DeletionRun(run)
                ? ReadRunSelection<VetPhotoOriginalSelection>(run.SelectionJson, capacity.MaxInputRevisions, 4096)?.Length == run.SelectedCount
                : ReadRunSelection<VetPhotoRunInputSnapshot>(run.SelectionJson, capacity.MaxInputRevisions, 768)?.Length == run.SelectedCount;
            return valid && review.State is "preview" or "preview_failed";
        }
        if (review.State != "accepted" || review.DecisionActorUserId != run.ActorUserId || !ReviewDelivered(review)) return false;
        var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w => w.RunId == run.Id && w.Ordinal == run.NextWindowOrdinal, ct);
        if (window == null) return false;
        if (window.ComparisonReviewId is { } compared)
        {
            var comparison = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == compared, ct);
            if (comparison == null || comparison.State is "stale" or "accepted") return false; // Actual accepted markers returned above.
        }
        if (DeletionRun(run))
        {
            var all = ReadRunSelection<VetPhotoOriginalSelection>(run.SelectionJson, capacity.MaxInputRevisions, 4096);
            var selected = ReadRunSelection<VetPhotoOriginalSelection>(window.SelectionJson, 50, 4096);
            if (all == null || selected == null || all.Length != run.SelectedCount || !selected.SequenceEqual(all.Skip(window.Ordinal * 50).Take(50))) return false;
            foreach (var row in selected) if (!await RunReferenceCurrentAsync(scope, row, false, ct)) return false;
            return true;
        }
        var full = ReadRunSelection<VetPhotoRunInputSnapshot>(run.SelectionJson, capacity.MaxInputRevisions, 768);
        var inputs = ReadRunSelection<VetPhotoRunInputSnapshot>(window.SelectionJson, 50, 768);
        if (full == null || inputs == null || full.Length != run.SelectedCount || window.Ordinal < 0
            || full.Select(s => s.AttemptKey).Distinct().Count() != full.Length
            || full.Select(s => s.InputRevisionId).Distinct().Count() != full.Length
            || !inputs.SequenceEqual(full.Skip(window.Ordinal * 50).Take(50))) return false;
        foreach (var input in inputs)
        {
            if (!await RunInputCurrentAsync(scope, input, ct)) return false;
            var attempt = await Scoped<VetPhotoAttempt>(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.AttemptKey, ct);
            if (attempt != null && (attempt.RunWindowId != window.Id || attempt.SourceId != input.SourceId || attempt.InputRevisionId != input.InputRevisionId
                || attempt.ActorUserId != run.ActorUserId || attempt.Kind != "image"
                || attempt.ExpectedCurrentInputId != input.ExpectedCurrentInputId || attempt.ExpectedSourceOrdinal != input.ExpectedSourceOrdinal)) return false;
            var other = await OtherImageAttemptsAsync(scope, input.SourceId, input.InputRevisionId, input.AttemptKey, ct);
            if (other.LiveCalls == 0 && other.Fingerprint != input.AcknowledgedUnknownFingerprint) return false;
        }
        return true;
    }
    private async Task<bool> RepairRunLockedAsync(VetDiaryScope scope, VetPhotoRun run, CancellationToken ct)
    {
        if (run.State is not ("preview" or "approved" or "running") || run.CancelledAt != null
            || !await ImageActorAsync(scope, run.ActorUserId, ct) || DeletionRun(run) && !await OwnerAsync(scope, run.ActorUserId, ct)
            || await RunRecoveryCurrentAsync(scope, run, ct)) return false;
        db.Attach(run); run.State = "stale"; return true;
    }
    private Task<bool> RepairScheduledRunAsync(VetDiaryScope scope, Guid runId, long actor, CancellationToken ct) =>
        WorkflowAsync(scope, actor, false, async () =>
        {
            var run = await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
            return run != null && await RepairRunLockedAsync(scope, run, ct);
        }, ct);
    private async Task RepairObviousRunsAsync(long familyId, long botDbId, CancellationToken ct)
    {
        _guard.Family(familyId); ForgetPhotoSnapshots(); var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await _guard.LockAsync(familyId, botDbId, ct); await _guard.BotAsync(familyId, botDbId, null, ct);
            var bot = await db.Bots.AsNoTracking().SingleOrDefaultAsync(b => b.Id == botDbId && b.FamilyId == familyId && b.Status == BotStatus.Active, ct);
            if (bot == null) { await tx.CommitAsync(ct); return; }
            var rows = await db.Set<VetPhotoRun>().AsNoTracking().Where(r => r.FamilyId == familyId && r.BotDbId == botDbId && r.TelegramBotId == bot.TelegramBotId
                && r.CancelledAt == null && (r.State == "preview" || r.State == "approved" || r.State == "running")
                && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == r.ActorUserId && m.Status == FamilyMemberStatus.Approved)
                && (r.ChatId == r.ActorUserId && r.TopicId == null || db.Places.Any(p => p.BotId == botDbId && p.ChatId == r.ChatId && p.TopicId == r.TopicId && p.Status == PlaceStatus.Approved))
                && (!db.Set<VetPhotoReview>().Any(v => v.Id == r.SelectionReviewId && v.FamilyId == familyId && v.BotDbId == botDbId && v.TelegramBotId == bot.TelegramBotId
                    && v.ChatId == r.ChatId && v.TopicId == r.TopicId && v.SelectionJson == r.SelectionJson
                    && (r.State == "preview" && (v.State == "preview" || v.State == "preview_failed")
                        || r.State != "preview" && v.State == "accepted" && v.CompletePreviewDelivered && v.DecisionActorUserId == r.ActorUserId)
                    && db.Set<VetProfile>().Any(p => p.Id == v.ProfileId && p.FamilyId == familyId && p.BotDbId == botDbId && p.Revision == v.ProfileRevision))
                    || db.Set<VetPhotoRunWindow>().Any(w => w.RunId == r.Id && w.Ordinal == r.NextWindowOrdinal
                        && db.Set<VetPhotoAttempt>().Any(a => a.RunWindowId == w.Id && a.Kind == "image"
                            && a.FamilyId == familyId && a.BotDbId == botDbId && a.TelegramBotId == bot.TelegramBotId
                            && (db.Set<VetPhotoSource>().Any(source => source.Id == a.SourceId && source.FamilyId == familyId && source.BotDbId == botDbId
                                && source.TelegramBotId == bot.TelegramBotId && source.ChatId == r.ChatId && source.TopicId == r.TopicId
                                && (source.CurrentInputRevisionId != a.ExpectedCurrentInputId || source.CurrentOrdinal != a.ExpectedSourceOrdinal))
                                || !db.Set<VetPhotoOriginalReference>().Any(original => original.InputRevisionId == a.InputRevisionId && original.FamilyId == familyId
                                    && original.BotDbId == botDbId && original.TelegramBotId == bot.TelegramBotId && original.ChatId == r.ChatId && original.TopicId == r.TopicId && original.State == "retained"))))))
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(MaxRunRepairsPerPoll).ToListAsync(ct);
            foreach (var run in rows) await RepairRunLockedAsync(new(familyId, botDbId, bot.TelegramBotId, run.ChatId, run.TopicId), run, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(CancellationToken.None); DetachOwned(before); throw; }
    }
}
