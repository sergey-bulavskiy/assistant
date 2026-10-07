using System.Globalization;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoWorkflowStore
{
    private async Task<T> WorkflowAsync<T>(VetDiaryScope scope, long actor, T refused,
        Func<Task<T>> action, CancellationToken ct, bool capacityLock = false)
    {
        await CheckAsync(scope, ct);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (capacityLock) await CapacityLockAsync(ct);
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            var privatePlace = scope.ChatId == actor && scope.TopicId is null ? "private" : null;
            if (!await ActorAsync(scope, actor, ct, privatePlace)) return refused;
            var result = await action();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }

    private static bool TerminalBatch(VetPhotoBatch batch) => batch.State is "cancelled" or "completed";
    private Task<VetPhotoBatch?> BatchAsync(VetDiaryScope scope, Guid id, CancellationToken ct) =>
        Scoped<VetPhotoBatch>(scope).AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);
    private Task<VetProfile?> WorkflowProfileAsync(VetDiaryScope scope, CancellationToken ct) =>
        db.Set<VetProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct);
    private async Task TouchBatchAsync(VetDiaryScope scope, VetPhotoBatch batch, CancellationToken ct)
    {
        await InvalidateBatchAsync(scope, batch.Id, ct);
        batch.ReviewRevision++;
        batch.UpdatedAt = clock.UtcNow;
    }

    public Task<VetPhotoBatchChange> StartCollectionAsync(VetDiaryScope scope, long actorUserId, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null),
            () => StartCollectionCoreAsync(scope, actorUserId, ct), ct);

    private async Task<VetPhotoBatchChange> StartCollectionCoreAsync(VetDiaryScope scope, long actorUserId, CancellationToken ct)
    {
            var existing = await Scoped<VetPhotoBatch>(scope).AsNoTracking().SingleOrDefaultAsync(b => b.State == "collecting", ct);
            if (existing is not null) return new(VetPhotoWorkflowStatus.Existing, existing);
            var profile = await WorkflowProfileAsync(scope, ct);
            if (profile is null) return new(VetPhotoWorkflowStatus.Refused, null);
            var batch = InScope(new VetPhotoBatch { Id = Guid.NewGuid(), ProfileId = profile.Id,
                ProfileRevision = profile.Revision, StarterUserId = actorUserId, State = "collecting", IntakeKind = "collection",
                AssumptionsJson = JsonSerializer.Serialize(new VetPhotoBatchAssumptions(profile.TimeZone, profile.GlucoseUnit,
                    null, null, null, false, false, false), Json), CreatedAt = clock.UtcNow,
                IntakeOpenedAt = clock.UtcNow, UpdatedAt = clock.UtcNow }, scope);
            db.Add(batch);
            return new(VetPhotoWorkflowStatus.Applied, batch);
    }

    public Task<VetPhotoBatchChange> CloseCollectionAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, long actorUserId, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null),
            () => CloseCollectionCoreAsync(scope, batchId, expectedRevision, actorUserId, ct), ct);

    private async Task<VetPhotoBatchChange> CloseCollectionCoreAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, long actorUserId, CancellationToken ct)
    {
            var batch = await BatchAsync(scope, batchId, ct);
            if (batch is null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (batch.State == "closed") return new(VetPhotoWorkflowStatus.Existing, batch);
            if (batch.State != "collecting" || batch.ReviewRevision != expectedRevision)
                return new(VetPhotoWorkflowStatus.Stale, batch);
            db.Attach(batch);
            batch.State = "closed"; batch.ClosedAt = clock.UtcNow; batch.IntakeClosedAt = clock.UtcNow;
            await TouchBatchAsync(scope, batch, ct);
            return new(VetPhotoWorkflowStatus.Applied, batch);
    }

    public Task<VetPhotoBatchSnapshot?> GetBatchAsync(VetDiaryScope scope, Guid batchId, long actorUserId, CancellationToken ct) =>
        WorkflowAsync<VetPhotoBatchSnapshot?>(scope, actorUserId, null, async () =>
        {
            var batch = await BatchAsync(scope, batchId, ct);
            if (batch is null) return null;
            var sources = await Scoped<VetPhotoSource>(scope).AsNoTracking().Where(s => s.BatchId == batchId).OrderBy(s => s.ItemNumber).ToListAsync(ct);
            var items = new List<VetPhotoBatchItem>();
            foreach (var source in sources)
            {
                var input = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking().SingleAsync(i => i.Id == source.CurrentInputRevisionId, ct);
                var candidate = await Scoped<VetPhotoCandidate>(scope).AsNoTracking().SingleAsync(c => c.SourceId == source.Id && c.CandidateOrdinal == 0, ct);
                items.Add(new(source, input, candidate));
            }
            return new(batch, await BatchCountsAsync(scope, batchId, ct), items.AsReadOnly());
        }, ct);

    public Task<VetPhotoBatchHistory> ListBatchesAsync(VetDiaryScope scope, long actorUserId, int offset, int limit, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchHistory([], null), async () =>
        {
            if (offset < 0 || limit is < 1 or > 50) return new([], null);
            var batches = await Scoped<VetPhotoBatch>(scope).AsNoTracking().OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
                .Skip(offset).Take(limit + 1).ToListAsync(ct);
            var summaries = new List<VetPhotoBatchSummary>();
            foreach (var batch in batches.Take(limit)) summaries.Add(new(batch, await BatchCountsAsync(scope, batch.Id, ct)));
            return new(summaries.AsReadOnly(), batches.Count > limit ? offset + limit : null);
        }, ct);

    private async Task<VetPhotoBatchCounts> BatchCountsAsync(VetDiaryScope scope, Guid batchId, CancellationToken ct)
    {
        var sources = Scoped<VetPhotoSource>(scope).Where(s => s.BatchId == batchId);
        var candidates = Scoped<VetPhotoCandidate>(scope).Where(c => c.BatchId == batchId);
        var attempts = Scoped<VetPhotoAttempt>(scope);
        var current = sources.Select(s => s.CurrentInputRevisionId);
        var sourceIds = sources.Select(s => s.Id);
        return new(await sources.CountAsync(ct), await sources.CountAsync(s => s.SourceMessageDbId != null, ct),
            await Scoped<VetPhotoOriginalReference>(scope).CountAsync(r => current.Contains(r.InputRevisionId) && r.State == "retained", ct),
            await candidates.CountAsync(c => c.State == "waiting", ct),
            await Scoped<VetPhotoExtraction>(scope).Where(e => current.Contains(e.InputRevisionId)).Select(e => e.SourceId).Distinct().CountAsync(ct),
            await candidates.CountAsync(c => c.State == "clear", ct), await candidates.CountAsync(c => c.State == "pending", ct),
            await sources.CountAsync(s => candidates.Any(c => c.SourceId == s.Id && c.State == "failed")
                || attempts.Any(a => a.SourceId == s.Id && a.InputRevisionId == s.CurrentInputRevisionId && a.State == "failed"), ct),
            await candidates.CountAsync(c => c.State == "saved", ct), await candidates.CountAsync(c => c.State == "excluded", ct),
            await candidates.CountAsync(c => c.State == "cancelled", ct),
            await Scoped<VetPhotoSource>(scope).CountAsync(s => s.ProposedBatchId == batchId && s.BatchId == null && (s.State == "late" || s.State == "full"), ct));
    }

    public Task<VetPhotoBatchChange> CancelRemainderAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, long actorUserId, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var batch = await BatchAsync(scope, batchId, ct);
            if (batch is null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (batch.State == "cancelled") return new(VetPhotoWorkflowStatus.Existing, batch);
            if (TerminalBatch(batch) || batch.ReviewRevision != expectedRevision) return new(VetPhotoWorkflowStatus.Stale, batch);
            var remainder = Scoped<VetPhotoCandidate>(scope).Where(c => c.BatchId == batchId && c.EventId == null && c.State != "saved");
            var ids = await remainder.Select(c => c.SourceId).ToListAsync(ct);
            await remainder.ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "cancelled")
                .SetProperty(c => c.RequiresExplicitRestoration, true).SetProperty(c => c.Revision, c => c.Revision + 1)
                .SetProperty(c => c.UpdatedAt, clock.UtcNow), ct);
            var attempts = await Scoped<VetPhotoAttempt>(scope).Where(a => ids.Contains(a.SourceId) && a.Kind == "image"
                && (a.State == "queued" || a.State == "retry_wait" || a.State == "capacity_wait" || a.State == "rate_wait")).ToListAsync(ct);
            foreach (var attempt in attempts) { ReleaseReservation(attempt); attempt.State = "cancelled"; attempt.UpdatedAt = clock.UtcNow; }
            db.Attach(batch); batch.State = "cancelled"; batch.CancelledAt = clock.UtcNow;
            batch.IntakeClosedAt ??= clock.UtcNow; batch.ClosedAt ??= clock.UtcNow;
            await TouchBatchAsync(scope, batch, ct);
            return new(VetPhotoWorkflowStatus.Applied, batch);
        }, ct, capacityLock: true);

    public Task<VetPhotoBatchChange> ChangeAssumptionAsync(VetPhotoAssumptionChange change, CancellationToken ct) =>
        WorkflowAsync(change.Scope, change.ActorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null),
            () => ChangeAssumptionCoreAsync(change, ct), ct);

    private async Task<VetPhotoBatchChange> ChangeAssumptionCoreAsync(VetPhotoAssumptionChange change, CancellationToken ct)
    {
            var batch = await BatchAsync(change.Scope, change.BatchId, ct);
            var profile = await WorkflowProfileAsync(change.Scope, ct);
            if (batch is null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (batch.ReviewRevision != change.ExpectedBatchRevision
                || profile is null || profile.Id != batch.ProfileId || profile.Revision != change.ExpectedProfileRevision)
                return new(VetPhotoWorkflowStatus.Stale, batch);
            var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, Json)!;
            switch (change.Kind)
            {
                case VetPhotoAssumptionKind.Year:
                    if (!int.TryParse(change.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 1 or > 9999)
                        return new(VetPhotoWorkflowStatus.Refused, batch);
                    assumptions = assumptions with { Year = year, YearConfirmed = true }; break;
                case VetPhotoAssumptionKind.Unit:
                    if (VetPhotoValidationRules.NormalizeUnit(change.Value) != "mmol/L") return new(VetPhotoWorkflowStatus.Refused, batch);
                    assumptions = assumptions with { GlucoseUnit = "mmol/L", UnitConfirmed = true }; break;
                case VetPhotoAssumptionKind.TimeZone:
                    if (string.IsNullOrWhiteSpace(change.Value) || change.Value.Length > 256) return new(VetPhotoWorkflowStatus.Refused, batch);
                    try { _ = TimeZoneInfo.FindSystemTimeZoneById(change.Value); }
                    catch (TimeZoneNotFoundException) { return new(VetPhotoWorkflowStatus.Refused, batch); }
                    catch (InvalidTimeZoneException) { return new(VetPhotoWorkflowStatus.Refused, batch); }
                    assumptions = assumptions with { TimeZone = change.Value, TimeZoneConfirmed = true }; break;
                default: return new(VetPhotoWorkflowStatus.Refused, batch);
            }
            db.Attach(batch); batch.AssumptionsJson = JsonSerializer.Serialize(assumptions, Json);
            // Adopt the observed revision only; frozen defaults remain visible and unchanged.
            batch.ProfileRevision = profile.Revision;
            await TouchBatchAsync(change.Scope, batch, ct);
            return new(VetPhotoWorkflowStatus.Applied, batch);
    }

    public Task<VetPhotoWorkflowStatus> ChangeCandidateAsync(VetPhotoCandidateChange change, CancellationToken ct) =>
        WorkflowAsync(change.Scope, change.ActorUserId, VetPhotoWorkflowStatus.Refused,
            () => ChangeCandidateCoreAsync(change, ct), ct);

    private async Task<VetPhotoWorkflowStatus> ChangeCandidateCoreAsync(VetPhotoCandidateChange change, CancellationToken ct)
    {
            var batch = await BatchAsync(change.Scope, change.BatchId, ct);
            var candidate = await Scoped<VetPhotoCandidate>(change.Scope).AsNoTracking().SingleOrDefaultAsync(c => c.Id == change.CandidateId && c.BatchId == change.BatchId, ct);
            if (batch is null || candidate is null) return VetPhotoWorkflowStatus.NotFound;
            var source = await Scoped<VetPhotoSource>(change.Scope).AsNoTracking().SingleAsync(s => s.Id == candidate.SourceId, ct);
            if (TerminalBatch(batch) && change.Kind is not (VetPhotoCandidateChangeKind.Correct or VetPhotoCandidateChangeKind.Restore)
                || batch.ReviewRevision != change.ExpectedBatchRevision || candidate.Revision != change.ExpectedCandidateRevision
                || source.CurrentInputRevisionId != change.ExpectedCurrentInputId || source.CurrentOrdinal != change.ExpectedSourceOrdinal
                || candidate.ExtractionResultId != change.ExpectedExtractionId) return VetPhotoWorkflowStatus.Stale;
            if (change.Kind == VetPhotoCandidateChangeKind.Exclude && candidate.EventId != null) return VetPhotoWorkflowStatus.Refused;
            if (change.Kind is VetPhotoCandidateChangeKind.Correct or VetPhotoCandidateChangeKind.Restore)
            {
                if (change.Reading is null || !VetPhotoReviewFormatter.Format("Проверка", [],
                    [new(1, VetPhotoReviewSection.Clear, change.Reading, "Исправление")], "Проверка").Success)
                    return VetPhotoWorkflowStatus.Refused;
            }
            if (change.Kind == VetPhotoCandidateChangeKind.DuplicateExisting)
            {
                if (change.DuplicateSourceId is null && change.DuplicateEventId is null) return VetPhotoWorkflowStatus.Refused;
                if (change.DuplicateSourceId is { } otherSource && (otherSource == source.Id
                    || !await Scoped<VetPhotoSource>(change.Scope).AnyAsync(s => s.Id == otherSource, ct)))
                    return VetPhotoWorkflowStatus.Refused;
                if (change.DuplicateEventId is { } eventId && !await db.Set<VetEvent>().AnyAsync(e => e.Id == eventId
                    && e.FamilyId == change.Scope.FamilyId && e.BotDbId == change.Scope.BotDbId
                    && e.ChatId == change.Scope.ChatId && e.TopicId == change.Scope.TopicId
                    && e.ProfileId == batch.ProfileId && e.DeletedAt == null && e.Revision == change.DuplicateEventRevision, ct))
                    return VetPhotoWorkflowStatus.Stale;
            }
            if (!Enum.IsDefined(change.Kind)) return VetPhotoWorkflowStatus.Refused;
            db.Attach(candidate);
            candidate.Revision++; candidate.InputRevisionId = source.CurrentInputRevisionId;
            candidate.UpdatedAt = clock.UtcNow;
            if (change.Kind == VetPhotoCandidateChangeKind.Exclude)
            { candidate.State = "excluded"; candidate.RequiresExplicitRestoration = true; }
            else
            {
                candidate.State = "pending";
                if (change.Kind is VetPhotoCandidateChangeKind.Correct or VetPhotoCandidateChangeKind.Restore)
                {
                    candidate.ManuallyCorrected = true;
                    if (change.Kind == VetPhotoCandidateChangeKind.Restore) candidate.RequiresExplicitRestoration = true;
                    candidate.EffectiveJson = JsonSerializer.Serialize(change.Reading, Json);
                    candidate.CorrectionProvenanceJson = JsonSerializer.Serialize(new { actorUserId = change.ActorUserId,
                        at = clock.UtcNow, priorRevision = change.ExpectedCandidateRevision, kind = change.Kind.ToString() }, Json);
                    // Exclusion/restoration remains a proposal; commit must explicitly authorize restoring a fact.
                }
                else
                {
                    candidate.DuplicateDecision = change.Kind == VetPhotoCandidateChangeKind.DuplicateExisting ? "keep_existing" : "separate";
                    candidate.DuplicateSourceId = change.DuplicateSourceId;
                    candidate.DuplicateEventId = change.DuplicateEventId;
                    candidate.DuplicateEventRevision = change.DuplicateEventRevision;
                }
            }
            db.Attach(batch); await TouchBatchAsync(change.Scope, batch, ct);
            return VetPhotoWorkflowStatus.Applied;
    }

    public Task<VetPhotoBatchChange> AddLateSourceAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, Guid sourceId, Guid expectedCurrentInputId, long actorUserId, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null),
            () => AddLateSourceCoreAsync(scope, batchId, expectedRevision, sourceId, expectedCurrentInputId, actorUserId, ct), ct);

    private async Task<VetPhotoBatchChange> AddLateSourceCoreAsync(VetDiaryScope scope, Guid batchId, int expectedRevision, Guid sourceId, Guid expectedCurrentInputId, long actorUserId, CancellationToken ct)
    {
            var batch = await BatchAsync(scope, batchId, ct);
            var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId, ct);
            if (batch is null || source is null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (source.BatchId == batchId) return new(VetPhotoWorkflowStatus.Existing, batch);
            if (TerminalBatch(batch) || batch.ReviewRevision != expectedRevision || source.CurrentInputRevisionId != expectedCurrentInputId)
                return new(VetPhotoWorkflowStatus.Stale, batch);
            if (source.BatchId != null || source.ProposedBatchId != batchId || source.State is not ("late" or "full"))
                return new(VetPhotoWorkflowStatus.Refused, batch);
            if (await Scoped<VetPhotoSource>(scope).CountAsync(s => s.BatchId == batchId, ct) >= 50)
                return new(VetPhotoWorkflowStatus.Full, batch);
            var candidate = await Scoped<VetPhotoCandidate>(scope).SingleAsync(c => c.SourceId == sourceId && c.CandidateOrdinal == 0, ct);
            db.Attach(source); source.BatchId = batchId; source.ProposedBatchId = null; source.ItemNumber = batch.NextItemNumber;
            source.Association = "explicit_late"; source.State = "admitted";
            candidate.BatchId = batchId; candidate.State = "waiting"; candidate.Revision++; candidate.UpdatedAt = clock.UtcNow;
            if (!await Scoped<VetPhotoAttempt>(scope).AnyAsync(a => a.InputRevisionId == expectedCurrentInputId && a.Kind == "download", ct))
                db.Add(InScope(new VetPhotoAttempt { Id = Guid.NewGuid(), SourceId = sourceId, InputRevisionId = expectedCurrentInputId,
                    ActorUserId = actorUserId, Kind = "download", State = "queued", ExpectedCurrentInputId = source.CurrentInputRevisionId,
                    ExpectedSourceOrdinal = source.CurrentOrdinal, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow }, scope));
            db.Attach(batch); batch.NextItemNumber++; await TouchBatchAsync(scope, batch, ct);
            return new(VetPhotoWorkflowStatus.Applied, batch);
    }

}
