using System.Globalization;
using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoRunStore
{
    private static bool DeletionRun(VetPhotoRun run) => run.SelectionMode.StartsWith("deletion_", StringComparison.Ordinal);
    private static string RunMode(VetPhotoRunSelectionRequest r) =>
        (r.Purpose == VetPhotoRunPurpose.DeleteOriginals ? "deletion_" : "") + (r.Mode switch
        {
            VetPhotoRunSelectionMode.Current => "current", VetPhotoRunSelectionMode.AllOriginals => "all_originals",
            VetPhotoRunSelectionMode.Selected => "selected", _ => "invalid"
        });
    private static string RunHeading(VetPhotoRunSelectionRequest r) =>
        $"{(r.Purpose == VetPhotoRunPurpose.DeleteOriginals ? "Удаление оригиналов" : "Повторное распознавание")}. " +
        $"Область: {(r.BatchId is { } batch ? $"пакет {batch:D}" : "текущее место")}. " +
        $"Политика: {r.Mode switch { VetPhotoRunSelectionMode.Current => "текущие версии", VetPhotoRunSelectionMode.AllOriginals => "все сохранённые версии", _ => "выбранные версии" }}. " +
        $"Даты: {(r.DateAxis == VetPhotoRunDateAxis.Upload ? "загрузка" : "измерение")}; " +
        $"с {r.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "начала"} " +
        $"по {r.UntilDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "конец"}. " +
        (r.Purpose == VetPhotoRunPurpose.Reprocess ? $"Провайдер: {r.ProviderName}; модель: {r.ModelName}." : "Только сохранённые приложением байты.");
    private static bool RunRequestValid(VetPhotoRunSelectionRequest r) => r.OperationKey != Guid.Empty
        && r.ActorUserId > 0 && r.ExpectedProfileRevision > 0 && Enum.IsDefined(r.Purpose)
        && Enum.IsDefined(r.Mode) && Enum.IsDefined(r.DateAxis)
        && (r.FromDate == null || r.UntilDate == null || r.FromDate <= r.UntilDate)
        && r.UntilDate != DateOnly.MaxValue
        && (r.Mode != VetPhotoRunSelectionMode.AllOriginals || r.BatchId == null && r.FromDate == null
            && r.UntilDate == null && r.ReferenceIds is null or { Count: 0 })
        && (r.Mode != VetPhotoRunSelectionMode.Selected || r.ReferenceIds is { Count: > 0 })
        && (r.Mode == VetPhotoRunSelectionMode.Selected || r.ReferenceIds is null or { Count: 0 })
        && (r.ReferenceIds == null || r.ReferenceIds.All(x => x != Guid.Empty)
            && r.ReferenceIds.Distinct().Count() == r.ReferenceIds.Count)
        && r.ModelName != null && r.ModelName.Length <= 200 && !r.ModelName.Contains('\0')
        && r.ProviderName != null && r.ProviderName.Length <= 200 && !r.ProviderName.Contains('\0')
        && (r.Purpose != VetPhotoRunPurpose.Reprocess || !string.IsNullOrWhiteSpace(r.ModelName) && !string.IsNullOrWhiteSpace(r.ProviderName));

    private T[]? ReadRunSelection<T>(string text, int max, int entryChars)
    {
        if (!VetPhotoReviewBounds.TrySelectionChars(max, out var bound) || !WorkflowTextBound(text, bound)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() is < 1
                || doc.RootElement.GetArrayLength() > max || doc.RootElement.EnumerateArray().Any(x =>
                    x.ValueKind != JsonValueKind.Object || x.GetRawText().Length > entryChars)) return null;
            var values = JsonSerializer.Deserialize<T[]>(text, Json);
            return values != null && values.All(x => x != null) ? values : null;
        }
        catch (JsonException) { return null; }
    }
    private async Task<VetPhotoRun?> OwnedRunAsync(VetPhotoRunHandle h, CancellationToken ct, bool requireProfileRevision = true)
    {
        if (!await ImageActorAsync(h.Scope, h.ActorUserId, ct)) return null;
        var run = await Scoped<VetPhotoRun>(h.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == h.RunId, ct);
        if (run == null || DeletionRun(run) && !await OwnerAsync(h.Scope, h.ActorUserId, ct)) return null;
        if (!requireProfileRevision && run.State == "stale") return run;
        var review = await Scoped<VetPhotoReview>(h.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.SelectionReviewId, ct);
        var profile = await WorkflowProfileAsync(h.Scope, ct);
        return review != null && profile != null && review.ProfileId == profile.Id && (!requireProfileRevision || review.ProfileRevision == profile.Revision)
            && review.Kind == (DeletionRun(run) ? "delete_originals_selection" : "reextract_selection")
            && review.SelectionJson == run.SelectionJson && (run.State is "preview" or "cancelled" || review.State == "accepted" && ReviewDelivered(review))
            && (review.State != "accepted" || review.DecisionActorUserId == run.ActorUserId)
            ? run : null;
    }
    private async Task<bool> RunReferenceCurrentAsync(VetDiaryScope scope, VetPhotoOriginalSelection s,
        bool checkBlobCount, CancellationToken ct)
    {
        if (!await Scoped<VetPhotoOriginalReference>(scope).AnyAsync(r => r.Id == s.ReferenceId
            && r.Revision == s.Revision && r.InputRevisionId == s.InputRevisionId && r.BlobId == s.BlobId
            && r.State == "retained", ct) || !await Scoped<VetPhotoInputRevision>(scope).AnyAsync(i =>
                i.Id == s.InputRevisionId && i.SourceId == s.SourceId, ct)
            || !await Scoped<VetPhotoSource>(scope).AnyAsync(x => x.Id == s.SourceId
                && x.CurrentInputRevisionId == s.ExpectedCurrentInputId && x.CurrentOrdinal == s.ExpectedSourceOrdinal, ct)
            || !await db.Set<VetPhotoBlob>().AnyAsync(b => b.Id == s.BlobId && b.FamilyId == scope.FamilyId && b.Content != null, ct)) return false;
        return !checkBlobCount || await db.Set<VetPhotoOriginalReference>().LongCountAsync(r =>
            r.FamilyId == scope.FamilyId && r.BlobId == s.BlobId && r.State == "retained", ct) == s.ExpectedBlobRetainedReferences;
    }
    private async Task<bool> RunInputCurrentAsync(VetDiaryScope scope, VetPhotoRunInputSnapshot s, CancellationToken ct)
    {
        if (s.AttemptKey == Guid.Empty || !await Scoped<VetPhotoSource>(scope).AnyAsync(x => x.Id == s.SourceId
                && x.CurrentInputRevisionId == s.ExpectedCurrentInputId && x.CurrentOrdinal == s.ExpectedSourceOrdinal, ct)
            || !await Scoped<VetPhotoOriginalReference>(scope).AnyAsync(r => r.Id == s.OriginalReferenceId
                && r.Revision == s.ExpectedReferenceRevision && r.InputRevisionId == s.InputRevisionId && r.State == "retained", ct)
            || !await RetainedInputAsync(scope, s.SourceId, s.InputRevisionId, ct)) return false;
        return s.ExpectedCandidateRevision == null || await Scoped<VetPhotoCandidate>(scope).AnyAsync(c =>
            c.SourceId == s.SourceId && c.CandidateOrdinal == 0 && c.Revision == s.ExpectedCandidateRevision, ct);
    }

    public Task<VetPhotoRunChange> StageRunAsync(VetPhotoRunSelectionRequest request, CancellationToken ct) =>
        WorkflowAsync(request.Scope, request.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            if (!capacity.IsValid || !VetPhotoReviewBounds.TrySelectionChars(capacity.MaxInputRevisions, out _)
                || !RunRequestValid(request) || !WorkflowTextBound(request.ModelName, 200) || !WorkflowTextBound(request.ProviderName, 200)
                || request.Purpose == VetPhotoRunPurpose.DeleteOriginals && !await OwnerAsync(request.Scope, request.ActorUserId, ct))
                return new(VetPhotoWorkflowStatus.Refused, null);
            if (request.ReferenceIds is { } requestedIds && requestedIds.Count > capacity.MaxInputRevisions) return new(VetPhotoWorkflowStatus.Full, null);
            var profile = await WorkflowProfileAsync(request.Scope, ct);
            if (profile == null || profile.Revision != request.ExpectedProfileRevision) return new(VetPhotoWorkflowStatus.Stale, null);
            if (request.BatchId is { } batchId && !await Scoped<VetPhotoBatch>(request.Scope).AnyAsync(b =>
                b.Id == batchId && b.ProfileId == profile.Id, ct)) return new(VetPhotoWorkflowStatus.NotFound, null);
            var mode = RunMode(request);
            var existing = await Scoped<VetPhotoRun>(request.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);
            if (existing != null)
            {
                var prior = await Scoped<VetPhotoReview>(request.Scope).AsNoTracking().SingleAsync(r => r.Id == existing.SelectionReviewId, ct);
                var pages = JsonSerializer.Deserialize<string[]>(prior.PreviewPagesJson, Json);
                var ids = DeletionRun(existing)
                    ? ReadRunSelection<VetPhotoOriginalSelection>(existing.SelectionJson, capacity.MaxInputRevisions, 4096)?.Select(x => x.ReferenceId).ToArray()
                    : ReadRunSelection<VetPhotoRunInputSnapshot>(existing.SelectionJson, capacity.MaxInputRevisions, 768)?.Select(x => x.OriginalReferenceId).ToArray();
                return prior.RequesterUserId == request.ActorUserId && existing.SelectionMode == mode && existing.ModelName == request.ModelName
                    && prior.BatchId == null && prior.ProfileId == profile.Id && prior.ProfileRevision == profile.Revision
                    && pages is { Length: > 0 } && pages[0].StartsWith(RunHeading(request), StringComparison.Ordinal)
                    && (request.Mode != VetPhotoRunSelectionMode.Selected || ids != null
                        && ids.Order().SequenceEqual(request.ReferenceIds!.Order()))
                    ? new(VetPhotoWorkflowStatus.Existing, existing, prior) : new(VetPhotoWorkflowStatus.Refused, null);
            }
            if (await Scoped<VetPhotoReview>(request.Scope).AnyAsync(r => r.OperationKey == request.OperationKey, ct))
                return new(VetPhotoWorkflowStatus.Refused, null);
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone ?? ""); }
            catch (TimeZoneNotFoundException) { return new(VetPhotoWorkflowStatus.Refused, null); }
            catch (InvalidTimeZoneException) { return new(VetPhotoWorkflowStatus.Refused, null); }
            var originals = new List<VetPhotoOriginalSelection>();
            var images = new List<VetPhotoRunInputSnapshot>();
            var sources = new HashSet<Guid>(); var unknown = new HashSet<Guid>();
            var uncertainSources = new Dictionary<Guid, (int MessageId, int Unknown, int Charged)>();
            var liveCalls = 0;
            var supersededExcluded = 0; var supersededIncluded = 0; var currentIncluded = 0;
            var query = from r in Scoped<VetPhotoOriginalReference>(request.Scope).AsNoTracking()
                join i in Scoped<VetPhotoInputRevision>(request.Scope).AsNoTracking() on r.InputRevisionId equals i.Id
                join s in Scoped<VetPhotoSource>(request.Scope).AsNoTracking() on i.SourceId equals s.Id
                where r.State == "retained" && (request.BatchId == null || s.BatchId == request.BatchId)
                    && (request.Mode != VetPhotoRunSelectionMode.Selected || request.ReferenceIds!.Contains(r.Id))
                orderby s.SentAt, s.Id, i.Ordinal, r.Id
                select new { Reference = r, Input = i, Source = s };
            var offset = 0;
            while (true)
            {
                var page = await query.Skip(offset).Take(50).ToListAsync(ct);
                if (page.Count == 0) break;
                offset += page.Count;
                foreach (var row in page)
                {
                    if (!await db.Set<VetPhotoBlob>().AnyAsync(b => b.Id == row.Reference.BlobId
                        && b.FamilyId == request.Scope.FamilyId && b.Content != null, ct)) continue;
                    var current = row.Input.Id == row.Source.CurrentInputRevisionId;
                    var candidate = await Scoped<VetPhotoCandidate>(request.Scope).AsNoTracking().SingleOrDefaultAsync(c =>
                        c.SourceId == row.Source.Id && c.CandidateOrdinal == 0, ct);
                    DateTimeOffset? measurement = null;
                    if (candidate?.InputRevisionId == row.Source.CurrentInputRevisionId)
                    {
                        try
                        {
                            var reading = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(candidate.EffectiveJson, Json);
                            if (reading is { Value: > 0 } && reading.OccurredAt != default && !string.IsNullOrWhiteSpace(reading.Unit)) measurement = reading.OccurredAt;
                        }
                        catch (JsonException) { }
                    }
                    if (measurement == null) unknown.Add(row.Source.Id);
                    if (request.Mode == VetPhotoRunSelectionMode.Current && !current) { supersededExcluded++; continue; }
                    var at = request.DateAxis == VetPhotoRunDateAxis.Upload ? row.Source.SentAt : measurement;
                    if (request.FromDate != null || request.UntilDate != null)
                    {
                        if (at == null) continue; // Counted above; summary names this exclusion and the upload/batch alternatives.
                        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at.Value, zone).DateTime);
                        if (request.FromDate is { } from && local < from || request.UntilDate is { } until && local > until) continue;
                    }
                    if (originals.Count == capacity.MaxInputRevisions) return new(VetPhotoWorkflowStatus.Full, null);
                    var retained = await db.Set<VetPhotoOriginalReference>().LongCountAsync(r =>
                        r.FamilyId == request.Scope.FamilyId && r.BlobId == row.Reference.BlobId && r.State == "retained", ct);
                    originals.Add(new(row.Reference.Id, row.Reference.Revision, row.Input.Id, row.Reference.BlobId,
                        row.Source.Id, row.Source.CurrentInputRevisionId, row.Source.CurrentOrdinal, retained, candidate?.EventId, candidate?.EventRevision));
                    var attemptFence = request.Purpose == VetPhotoRunPurpose.Reprocess
                        ? await OtherImageAttemptsAsync(request.Scope, row.Source.Id, row.Input.Id, null, ct) : null;
                    images.Add(new(row.Source.Id, row.Input.Id, row.Reference.Id, row.Reference.Revision,
                        row.Source.CurrentInputRevisionId, row.Source.CurrentOrdinal, null, Guid.NewGuid())
                        { AcknowledgedUnknownFingerprint = attemptFence?.Fingerprint });
                    if (attemptFence != null)
                    {
                        liveCalls += attemptFence.LiveCalls;
                        if (attemptFence.UnknownCount > 0)
                        {
                            uncertainSources.TryGetValue(row.Source.Id, out var priorCounts);
                            uncertainSources[row.Source.Id] = (row.Source.TelegramMessageId,
                                priorCounts.Unknown + attemptFence.UnknownCount, priorCounts.Charged + attemptFence.ChargedUnknownCount);
                        }
                    }
                    sources.Add(row.Source.Id); if (current) currentIncluded++; else supersededIncluded++;
                }
            }
            var deleted = await (from r in Scoped<VetPhotoOriginalReference>(request.Scope)
                join i in Scoped<VetPhotoInputRevision>(request.Scope) on r.InputRevisionId equals i.Id
                join s in Scoped<VetPhotoSource>(request.Scope) on i.SourceId equals s.Id
                where r.State == "deleted" && (request.BatchId == null || s.BatchId == request.BatchId)
                select s.Id).Distinct().CountAsync(ct);
            var counts = new VetPhotoRunSelectionCounts(sources.Count, originals.Count, currentIncluded,
                supersededIncluded, supersededExcluded, unknown.Count, deleted,
                uncertainSources.Values.Sum(x => x.Unknown), uncertainSources.Values.Sum(x => x.Charged), liveCalls);
            if (originals.Count == 0) return new(VetPhotoWorkflowStatus.NotFound, null, SelectionCounts: counts);
            if (request.Mode == VetPhotoRunSelectionMode.Selected && !originals.Select(x => x.ReferenceId).Order()
                .SequenceEqual(request.ReferenceIds!.Order())) return new(VetPhotoWorkflowStatus.Refused, null);
            var selection = request.Purpose == VetPhotoRunPurpose.DeleteOriginals
                ? JsonSerializer.Serialize(originals, Json) : JsonSerializer.Serialize(images, Json);
            if (request.Purpose == VetPhotoRunPurpose.Reprocess
                ? ReadRunSelection<VetPhotoRunInputSnapshot>(selection, capacity.MaxInputRevisions, 768) == null
                : ReadRunSelection<VetPhotoOriginalSelection>(selection, capacity.MaxInputRevisions, 4096) == null)
                return new(VetPhotoWorkflowStatus.Full, null);
            var windows = (originals.Count + 49) / 50;
            var summaryBlocks = new List<string>
                { $"Источников: {sources.Count}; сохранённых версий выбрано: {originals.Count}; текущих: {currentIncluded}; прежних: {supersededIncluded}. " +
                 $"Прежних версий вне текущей политики: {supersededExcluded}; источников без даты измерения: {unknown.Count}; источников с удалёнными байтами в области: {deleted}.",
                 $"Окон по 50 или меньше: {windows}. Ничего не обрезано. " +
                 (request.DateAxis == VetPhotoRunDateAxis.Measurement && (request.FromDate != null || request.UntilDate != null)
                     ? "Для диапазона измерений неизвестные даты исключены; доступен выбор по загрузке, пакету или всем оригиналам. " : "Неизвестные даты включены. ") +
                 (request.Purpose == VetPhotoRunPurpose.Reprocess ? $"Новых вызовов не более {originals.Count}; квота и срок сброса неизвестны; активных предыдущих вызовов: {liveCalls}." : "Начальное подтверждение не удаляет байты. Каждое окно показывает точные ссылки отдельно.") };
            if (uncertainSources.Count > 0)
            {
                summaryBlocks.Add($"Предыдущих неопределённых исходов: {counts.UnknownAttempts}; сохранённых резервов результата: {counts.ChargedUnknownAttempts}. " +
                    "Эти вызовы могли израсходовать квоту. Подтверждение этой области разрешает новые попытки по показанным источникам и возможный повторный расход квоты. " +
                    "Предыдущие исходы и их учёт сохраняются. Далее: номер источника: неопределённые исходы/сохранённые резервы результата.");
                var compact = new StringBuilder();
                foreach (var item in uncertainSources.Values.OrderBy(x => x.MessageId))
                {
                    var token = $"{item.MessageId}:{item.Unknown}/{item.Charged}; ";
                    if (compact.Length + token.Length > VetPhotoReviewFormatter.MaxPageChars)
                    { summaryBlocks.Add(compact.ToString()); compact.Clear(); }
                    compact.Append(token);
                }
                if (compact.Length > 0) summaryBlocks.Add(compact.ToString());
            }
            var preview = VetPhotoReviewFormatter.FormatBlocks(RunHeading(request), summaryBlocks,
                "Подтвердите область. Продолжение каждого окна явно; отмена останавливает новые окна и сохраняет результаты и факты." + (request.Purpose == VetPhotoRunPurpose.DeleteOriginals ? " Для повторной обработки этих оригиналов потребуется загрузить фото заново." : ""));
            if (!preview.Success) return new(VetPhotoWorkflowStatus.Refused, null);
            var review = InScope(new VetPhotoReview { Id = Guid.NewGuid(), OperationKey = request.OperationKey,
                Kind = request.Purpose == VetPhotoRunPurpose.Reprocess ? "reextract_selection" : "delete_originals_selection",
                // This is a source/reference selection, not a fact review under an old batch's assumptions.
                // The exact batch filter is shown in the frozen heading; each selected source/ref has its own proof.
                ProfileId = profile.Id,
                ProfileRevision = profile.Revision, RequesterUserId = request.ActorUserId, SelectionJson = selection,
                Fingerprint = Hash(Encoding.UTF8.GetBytes(selection)), PreviewPagesJson = JsonSerializer.Serialize(preview.Pages, Json),
                PageCount = preview.Pages.Count, CreatedAt = clock.UtcNow }, request.Scope);
            var run = InScope(new VetPhotoRun { Id = Guid.NewGuid(), OperationKey = request.OperationKey,
                SelectionReviewId = review.Id, SelectionMode = mode, SelectionJson = selection, ModelName = request.ModelName,
                ActorUserId = request.ActorUserId, SelectedCount = originals.Count, CreatedAt = clock.UtcNow }, request.Scope);
            db.Add(review); db.Add(run); return new(VetPhotoWorkflowStatus.Applied, run, review, SelectionCounts: counts);
        }, ct);

    public Task<VetPhotoRunChange> ApproveRunAsync(VetPhotoRunHandle handle, VetPhotoReviewHandle proof, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var run = await OwnedRunAsync(handle, ct);
            if (run == null || proof.Scope != handle.Scope || proof.ActorUserId != handle.ActorUserId
                || proof.ReviewId != run.SelectionReviewId) return new(VetPhotoWorkflowStatus.Stale, null);
            var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleAsync(r => r.Id == proof.ReviewId, ct);
            if (proof.OperationKey != run.OperationKey || proof.Revision != review.Revision
                || proof.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId
                || !ReviewDelivered(review)) return new(VetPhotoWorkflowStatus.Stale, run);
            if (review.State == "accepted" && review.DecisionActorUserId == handle.ActorUserId)
                return new(VetPhotoWorkflowStatus.Existing, run, review);
            if (run.State != "preview" || review.State != "preview" || !await ReviewCurrentAsync(handle.Scope, review, ct)
                || review.Kind != (DeletionRun(run) ? "delete_originals_selection" : "reextract_selection"))
                return new(VetPhotoWorkflowStatus.Stale, run);
            var windows = new List<string>();
            if (DeletionRun(run))
            {
                var selected = ReadRunSelection<VetPhotoOriginalSelection>(run.SelectionJson, capacity.MaxInputRevisions, 4096);
                if (selected == null || selected.Length != run.SelectedCount || selected.Select(x => x.ReferenceId).Distinct().Count() != selected.Length)
                    return new(VetPhotoWorkflowStatus.Refused, run);
                foreach (var s in selected) if (!await RunReferenceCurrentAsync(handle.Scope, s, true, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
                windows.AddRange(selected.Chunk(50).Select(x => JsonSerializer.Serialize(x, Json)));
            }
            else
            {
                var selected = ReadRunSelection<VetPhotoRunInputSnapshot>(run.SelectionJson, capacity.MaxInputRevisions, 768);
                if (selected == null || selected.Length != run.SelectedCount || selected.Select(x => x.AttemptKey).Distinct().Count() != selected.Length
                    || selected.Select(x => x.OriginalReferenceId).Distinct().Count() != selected.Length) return new(VetPhotoWorkflowStatus.Refused, run);
                foreach (var s in selected)
                {
                    if (!await RunInputCurrentAsync(handle.Scope, s, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
                    var fence = await OtherImageAttemptsAsync(handle.Scope, s.SourceId, s.InputRevisionId, s.AttemptKey, ct);
                    if (fence.LiveCalls > 0) return new(VetPhotoWorkflowStatus.Incomplete, run);
                    if (fence.Fingerprint != s.AcknowledgedUnknownFingerprint) return new(VetPhotoWorkflowStatus.Stale, run);
                }
                windows.AddRange(selected.Chunk(50).Select(x => JsonSerializer.Serialize(x, Json)));
            }
            if (windows.Any(x => !WorkflowTextBound(x, 65536))) return new(VetPhotoWorkflowStatus.Full, run);
            for (var i = 0; i < windows.Count; i++) db.Add(InScope(new VetPhotoRunWindow { Id = Guid.NewGuid(), RunId = run.Id,
                Ordinal = i, State = "awaiting_continue", SelectionJson = windows[i], CreatedAt = clock.UtcNow }, handle.Scope));
            db.Attach(run); run.State = "approved"; run.ActorUserId = handle.ActorUserId;
            db.Attach(review); review.State = "accepted"; review.DecisionActorUserId = handle.ActorUserId; review.DecidedAt = clock.UtcNow;
            return new(VetPhotoWorkflowStatus.Applied, run, review);
        }, ct);

    public Task<VetPhotoRunChange> ContinueRunAsync(VetPhotoRunHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var run = await OwnedRunAsync(handle, ct);
            if (run == null || run.ActorUserId != handle.ActorUserId || run.State is not ("approved" or "running") || run.CancelledAt != null)
                return new(VetPhotoWorkflowStatus.Stale, run);
            var window = await Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(w =>
                w.RunId == run.Id && w.Ordinal == run.NextWindowOrdinal, ct);
            if (window == null) return new(VetPhotoWorkflowStatus.NotFound, run);
            if (window.State != "awaiting_continue") return new(VetPhotoWorkflowStatus.Existing, run, Window: window);
            if (DeletionRun(run))
            {
                var selected = ReadRunSelection<VetPhotoOriginalSelection>(window.SelectionJson, 50, 4096);
                if (selected == null) return new(VetPhotoWorkflowStatus.Refused, run);
                var fresh = new List<VetPhotoOriginalSelection>(); var blocks = new List<string>();
                foreach (var s in selected)
                {
                    if (!await RunReferenceCurrentAsync(handle.Scope, s, false, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
                    var count = await db.Set<VetPhotoOriginalReference>().LongCountAsync(r =>
                        r.FamilyId == handle.Scope.FamilyId && r.BlobId == s.BlobId && r.State == "retained", ct);
                    var candidate = await Scoped<VetPhotoCandidate>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(c => c.SourceId == s.SourceId && c.CandidateOrdinal == 0, ct);
                    if (candidate?.EventId != s.EventId || candidate?.EventRevision != s.EventRevision) return new(VetPhotoWorkflowStatus.Stale, run);
                    fresh.Add(s with { ExpectedBlobRetainedReferences = count });
                    var source = await Scoped<VetPhotoSource>(handle.Scope).AsNoTracking().SingleAsync(x => x.Id == s.SourceId, ct);
                    var input = await Scoped<VetPhotoInputRevision>(handle.Scope).AsNoTracking().SingleAsync(x => x.Id == s.InputRevisionId, ct);
                    var reference = await Scoped<VetPhotoOriginalReference>(handle.Scope).AsNoTracking().SingleAsync(x => x.Id == s.ReferenceId, ct);
                    blocks.Add($"Источник {s.SourceId:D}, вход {s.InputRevisionId:D}. " + $"Источник №{source.TelegramMessageId}, версия {input.Ordinal}: {(input.Id == source.CurrentInputRevisionId ? "текущая" : "прежняя")}. " +
                        $"Оригинал {s.ReferenceId:D}, ревизия {s.Revision}; {reference.ActualBytes} байт; " +
                        $"сохранённых ссылок на общие байты: {count}; " + (s.EventId is { } eventId ? $"связанный факт №{eventId}, ревизия {s.EventRevision}." : "связанного факта нет.") +
                        " Факты, действия и происхождение сохраняются.");
                }
                var byBlob = fresh.GroupBy(x => x.BlobId); long reclaim = 0; var reclaimBlobs = 0;
                foreach (var group in byBlob)
                    if (group.First().ExpectedBlobRetainedReferences == group.Count())
                    { reclaimBlobs++; reclaim += await db.Set<VetPhotoBlob>().Where(b => b.Id == group.Key && b.FamilyId == handle.Scope.FamilyId).Select(b => b.ActualBytes).SingleAsync(ct); }
                var preview = VetPhotoReviewFormatter.FormatBlocks($"Удаление оригиналов: окно {window.Ordinal + 1}. Показано {fresh.Count} точных ссылок.", blocks,
                    $"После этого окна останется {run.SelectedCount - (window.Ordinal * 50 + fresh.Count)} ссылок. " +
                    $"Для освобождения доступно {reclaimBlobs} общих объектов, {reclaim} байт; активное чтение откладывает физическое освобождение. Подтвердите только показанное окно. Для повторной обработки этих оригиналов потребуется загрузить фото заново.");
                if (!preview.Success) return new(VetPhotoWorkflowStatus.Refused, run);
                var initial = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleAsync(r => r.Id == run.SelectionReviewId, ct);
                var selection = JsonSerializer.Serialize(fresh, Json);
                var review = InScope(new VetPhotoReview { Id = Guid.NewGuid(), OperationKey = Guid.NewGuid(), Kind = "delete_originals",
                    RunWindowId = window.Id, ProfileId = initial.ProfileId, ProfileRevision = initial.ProfileRevision,
                    RequesterUserId = handle.ActorUserId, SelectionJson = selection, Fingerprint = Hash(Encoding.UTF8.GetBytes(selection)),
                    PreviewPagesJson = JsonSerializer.Serialize(preview.Pages, Json), PageCount = preview.Pages.Count, CreatedAt = clock.UtcNow }, handle.Scope);
                db.Add(review); db.Attach(window); window.ComparisonReviewId = review.Id; window.State = "awaiting_review";
                db.Attach(run); run.State = "running"; return new(VetPhotoWorkflowStatus.Applied, run, review, window);
            }
            var images = ReadRunSelection<VetPhotoRunInputSnapshot>(window.SelectionJson, 50, 768);
            if (images == null) return new(VetPhotoWorkflowStatus.Refused, run);
            foreach (var s in images)
            {
                if (!await RunInputCurrentAsync(handle.Scope, s, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
                var fence = await OtherImageAttemptsAsync(handle.Scope, s.SourceId, s.InputRevisionId, s.AttemptKey, ct);
                if (fence.LiveCalls > 0) return new(VetPhotoWorkflowStatus.Incomplete, run, Window: window);
                if (fence.Fingerprint != s.AcknowledgedUnknownFingerprint) return new(VetPhotoWorkflowStatus.Stale, run, Window: window);
            }
            foreach (var s in images)
                db.Add(InScope(new VetPhotoAttempt { Id = s.AttemptKey, SourceId = s.SourceId, InputRevisionId = s.InputRevisionId,
                    RunWindowId = window.Id, ActorUserId = handle.ActorUserId, Kind = "image", State = "queued",
                    ExpectedCurrentInputId = s.ExpectedCurrentInputId, ExpectedSourceOrdinal = s.ExpectedSourceOrdinal,
                    HistoricalSelection = s.InputRevisionId != s.ExpectedCurrentInputId, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow }, handle.Scope));
            db.Attach(window); window.State = "queued"; db.Attach(run); run.State = "running";
            return new(VetPhotoWorkflowStatus.Applied, run, Window: window);
        }, ct);

    public Task<VetPhotoRunChange> CancelRunAsync(VetPhotoRunHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var run = await OwnedRunAsync(handle, ct, requireProfileRevision: false);
            if (run == null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (run.State is "cancelled" or "completed") return new(VetPhotoWorkflowStatus.Existing, run);
            var windows = await Scoped<VetPhotoRunWindow>(handle.Scope).Where(w => w.RunId == run.Id && w.State != "completed").ToListAsync(ct);
            var ids = windows.Select(w => w.Id).ToArray();
            var queued = await Scoped<VetPhotoAttempt>(handle.Scope).Where(a => ids.Contains(a.RunWindowId!.Value)
                && (a.State == "queued" || a.State == "claimed")).ToListAsync(ct);
            foreach (var a in queued) { a.State = "cancelled"; a.ClaimToken = null; a.LeaseUntil = null; ReleaseReservation(a); a.UpdatedAt = clock.UtcNow; }
            foreach (var w in windows) w.State = "cancelled";
            await Scoped<VetPhotoReview>(handle.Scope).Where(r => ids.Contains(r.RunWindowId!.Value) && r.State == "preview")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "stale").SetProperty(r => r.CompletePreviewDelivered, false), ct);
            db.Attach(run); run.State = "cancelled"; run.CancelledAt = clock.UtcNow;
            return new(VetPhotoWorkflowStatus.Applied, run);
        }, ct, capacityLock: true);

    public Task<VetPhotoRunProgress?> GetRunAsync(VetPhotoRunHandle handle, int offset, int limit, CancellationToken ct) =>
        WorkflowAsync<VetPhotoRunProgress?>(handle.Scope, handle.ActorUserId, null, async () =>
        {
            if (offset < 0 || limit is < 1 or > 50) return null;
            if (!await ImageActorAsync(handle.Scope, handle.ActorUserId, ct)) return null;
            var run = await Scoped<VetPhotoRun>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == handle.RunId, ct);
            if (run == null || DeletionRun(run) && !await OwnerAsync(handle.Scope, handle.ActorUserId, ct)) return null;
            await RepairRunLockedAsync(handle.Scope, run, ct);
            if (run.State != "stale" && await OwnedRunAsync(handle, ct, requireProfileRevision: false) == null) return null;
            var query = Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().Where(w => w.RunId == run.Id);
            var total = await query.CountAsync(ct); var completed = await query.CountAsync(w => w.State == "completed", ct);
            var page = await query.OrderBy(w => w.Ordinal).Skip(offset).Take(limit).ToListAsync(ct);
            return new VetPhotoRunProgress(run, total, completed, Math.Max(0, run.SelectedCount - completed * 50), page,
                offset + page.Count < total ? offset + page.Count : null);
        }, ct);

    public Task<VetPhotoRunChange> AttachComparisonAsync(VetPhotoRunHandle handle, Guid windowId, Guid reviewId, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var run = await OwnedRunAsync(handle, ct, requireProfileRevision: false);
            var window = await Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(w => w.Id == windowId && w.RunId == handle.RunId, ct);
            var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == reviewId, ct);
            if (run == null || DeletionRun(run) || run.State != "running" || run.CancelledAt != null || window == null
                || window.Ordinal != run.NextWindowOrdinal || review == null || review.RunWindowId != window.Id
                || review.Kind != "reextract_comparison"
                || !await ReviewCurrentAsync(handle.Scope, review, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
            if (window.ComparisonReviewId == reviewId) return new(VetPhotoWorkflowStatus.Existing, run, review, window);
            if (window.State is not ("queued" or "running") || window.ComparisonReviewId != null)
                return new(VetPhotoWorkflowStatus.Stale, run);
            var selected = ReadRunSelection<VetPhotoRunInputSnapshot>(window.SelectionJson, 50, 768);
            if (selected == null) return new(VetPhotoWorkflowStatus.Refused, run);
            foreach (var s in selected)
            {
                if (!await RunInputCurrentAsync(handle.Scope, s, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
                var attempt = await Scoped<VetPhotoAttempt>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == s.AttemptKey && a.RunWindowId == window.Id, ct);
                if (attempt == null || attempt.State is not ("returned" or "failed" or "unknown")
                    || attempt.State == "unknown" && !attempt.ReservedResultSlot
                    || attempt.State != "unknown" && attempt.ReservedResultSlot)
                    return new(VetPhotoWorkflowStatus.Incomplete, run);
                if (attempt.State == "returned" && !await Scoped<VetPhotoExtraction>(handle.Scope).AnyAsync(e => e.AttemptId == s.AttemptKey
                    && e.SourceId == s.SourceId && e.InputRevisionId == s.InputRevisionId, ct)) return new(VetPhotoWorkflowStatus.Incomplete, run);
            }
            db.Attach(window); window.ComparisonReviewId = review.Id; window.State = "awaiting_review";
            return new(VetPhotoWorkflowStatus.Applied, run, review, window);
        }, ct);

    public Task<VetPhotoRunChange> ReconcileWindowAsync(VetPhotoRunHandle handle, Guid windowId, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            var run = await OwnedRunAsync(handle, ct, requireProfileRevision: false);
            var window = await Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(w => w.Id == windowId && w.RunId == handle.RunId, ct);
            if (run == null || window == null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (window.State == "completed") return new(VetPhotoWorkflowStatus.Existing, run, Window: window);
            if (run.CancelledAt != null || run.State != "running" || window.Ordinal != run.NextWindowOrdinal
                || window.ComparisonReviewId == null) return new(VetPhotoWorkflowStatus.Stale, run);
            var review = await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == window.ComparisonReviewId, ct);
            if (review == null || review.RunWindowId != window.Id || review.State != "accepted" || !ReviewDelivered(review)
                || review.DecisionActorUserId is not > 0 || review.Kind != (DeletionRun(run) ? "delete_originals" : "reextract_comparison"))
                return new(VetPhotoWorkflowStatus.Incomplete, run);
            if (DeletionRun(run))
            {
                var selected = ReadRunSelection<VetPhotoOriginalSelection>(review.SelectionJson, 50, 4096);
                if (selected == null || review.OutcomeJson == null) return new(VetPhotoWorkflowStatus.Incomplete, run);
                var result = JsonSerializer.Deserialize<VetPhotoOriginalDeletionResult>(review.OutcomeJson, Json);
                if (result == null || result.Status != VetMutationStatus.Applied || result.ReferenceCount != selected.Length)
                    return new(VetPhotoWorkflowStatus.Incomplete, run);
                foreach (var s in selected) if (!await Scoped<VetPhotoOriginalReference>(handle.Scope).AnyAsync(r =>
                    r.Id == s.ReferenceId && r.State == "deleted" && r.Revision == s.Revision + 1, ct)) return new(VetPhotoWorkflowStatus.Stale, run);
            }
            else if (review.ActionId == null || !await Scoped<VetDiaryAction>(handle.Scope).AnyAsync(a =>
                a.Id == review.ActionId && a.ActorUserId == review.DecisionActorUserId, ct)) return new(VetPhotoWorkflowStatus.Incomplete, run);
            db.Attach(window); window.State = "completed"; window.CompletedAt = clock.UtcNow; window.ActionId = review.ActionId;
            db.Attach(run); run.NextWindowOrdinal++;
            if (!await Scoped<VetPhotoRunWindow>(handle.Scope).AnyAsync(w => w.RunId == run.Id && w.Ordinal == run.NextWindowOrdinal, ct)) run.State = "completed";
            return new(VetPhotoWorkflowStatus.Applied, run, review, window);
        }, ct);

    public async Task<VetPhotoDeletionWindowResult> ConfirmDeletionWindowAsync(VetPhotoRunHandle handle,
        Guid windowId, VetPhotoReviewHandle proof, CancellationToken ct)
    {
        // Archive owns its transaction. Accepted archive review is the durable crash-recovery marker.
        var permitted = await WorkflowAsync(handle.Scope, handle.ActorUserId, false, async () =>
        {
            var run = await OwnedRunAsync(handle, ct);
            var window = await Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(w => w.Id == windowId && w.RunId == handle.RunId, ct);
            return run != null && DeletionRun(run) && run.CancelledAt == null && run.State is "running" or "completed"
                && window != null && window.ComparisonReviewId == proof.ReviewId && window.State is "awaiting_review" or "completed"
                && proof.Scope == handle.Scope && proof.ActorUserId == handle.ActorUserId;
        }, ct);
        if (!permitted) return new(VetPhotoWorkflowStatus.Stale, null, null);
        var deletion = await DeleteOriginalsAsync(new(handle.Scope, proof.ReviewId, proof.Revision, proof.OperationKey,
            handle.ActorUserId, proof.CallbackPromptMessageId), ct);
        if (deletion.Status is not (VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied))
            return new(deletion.Status == VetMutationStatus.Refused ? VetPhotoWorkflowStatus.Refused : VetPhotoWorkflowStatus.Stale, deletion, null);
        var reconciled = await ReconcileWindowAsync(handle, windowId, ct);
        return new(reconciled.Status, deletion, reconciled.Run);
    }
}
