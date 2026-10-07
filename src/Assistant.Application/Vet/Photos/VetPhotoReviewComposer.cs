using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoComposedReview(VetPhotoWorkflowStatus Status, VetPhotoReview? Review,
    VetPhotoBatchSnapshot? Batch, string StatusText)
{
    public IReadOnlyList<string> Pages { get; init; } = [];
}

public sealed class VetPhotoReviewComposer(IVetPhotoWorkflowStore workflow,
    IVetPhotoPresentationStore presentation, IVetProfileStore profiles, IVetPhotoDiaryStore diary,
    ILogger<VetPhotoReviewComposer> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VetPhotoComposedReview> BuildBatchAsync(VetDiaryScope scope, Guid batchId,
        long actorUserId, IReadOnlyList<Guid>? selectedCandidateIds, bool explicitRestoration, CancellationToken ct, Guid? operationKey = null, bool durableEvidence = false)
    {
        var batch = await workflow.GetBatchAsync(scope, batchId, actorUserId, ct);
        if (batch == null) return new(VetPhotoWorkflowStatus.NotFound, null, null, "Партия не найдена в этом месте.");
        if (batch.Items.Count > 50) return new(VetPhotoWorkflowStatus.Refused, null, batch, "Состав партии превышает предел.");
        if (batch.Items.Count == 0) return new(VetPhotoWorkflowStatus.Incomplete, null, batch, "В партии пока нет фото.");
        if (selectedCandidateIds is { Count: > 50 } || selectedCandidateIds?.Distinct().Count() != selectedCandidateIds?.Count)
            return new(VetPhotoWorkflowStatus.Refused, null, batch, "Нужен точный список без повторов.");
        var profile = await profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, ct);
        var refreshed = await presentation.RefreshProfileSnapshotAsync(scope, batchId, batch.Batch.ReviewRevision,
            profile.Revision, actorUserId, ct);
        if (refreshed.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
            return new(refreshed.Status, null, batch, "Профиль или партия изменились; нужен новый просмотр.");
        batch = (await workflow.GetBatchAsync(scope, batchId, actorUserId, ct))!;
        if (batch.Batch.State == "collecting") return new(VetPhotoWorkflowStatus.Incomplete, null, batch,
            Status(batch) + "\nСначала явно завершите приём фото: /photos_close.");
        var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.Batch.AssumptionsJson, Json)!;
        foreach (var item in batch.Items)
        {
            if (item.Candidate.EventId != null || item.Candidate.ManuallyCorrected || item.Candidate.RequiresExplicitRestoration
                || item.Candidate.State is "linked" or "excluded" or "cancelled" or "deleted") continue;
            var evidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, item.Input.Id, null, actorUserId, ct);
            if (evidence?.Extraction == null || evidence.Extraction.State == "comparison") continue;
            var image = VetPhotoInterpretationParser.Parse(evidence.Extraction.StructuredJson, item.Source.Id, item.Input.Id);
            if (image == null) continue;
            var (context, captionFailure) = VetPhotoCaptionContext.Read(evidence.Caption);
            var validation = VetPhotoValidationRules.Validate(image, context, assumptions, item.Input.ReceivedAt);
            if (captionFailure is "ambiguous_caption_readings" or "uncertain_caption_reading" or "caption_date_requires_clarification" or "caption_processing")
                validation = new(null, [captionFailure]);
            var current = await workflow.GetBatchAsync(scope, batchId, actorUserId, ct);
            if (current == null) return new(VetPhotoWorkflowStatus.Stale, null, batch, "Партия изменилась.");
            var updated = current.Items.SingleOrDefault(i => i.Source.Id == item.Source.Id);
            if (updated == null) return new(VetPhotoWorkflowStatus.Stale, null, batch, "Состав партии изменился.");
            var validated = await presentation.SetValidationAsync(new(scope, batchId, current.Batch.ReviewRevision, updated.Candidate.Id,
                updated.Candidate.Revision, updated.Input.Id, evidence.Extraction.Id, profile.Revision,
                actorUserId, context, validation), ct);
            if (validated is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
                return new(validated, null, current, "Сведения подписи или кандидат изменились; нужен новый просмотр.");
        }
        batch = (await workflow.GetBatchAsync(scope, batchId, actorUserId, ct))!;
        if (batch.Batch.ProfileRevision != profile.Revision)
            return new(VetPhotoWorkflowStatus.Stale, null, batch, "Профиль изменился; нужен новый просмотр.");
        var rows = new List<VetPhotoReviewRow>();
        var evidenceBlocksByNumber = new Dictionary<int, IReadOnlyList<string>>();
        var selected = new List<VetPhotoDiarySelection>();
        var evidenceBySource = new Dictionary<Guid, VetPhotoPresentationEvidence>();
        foreach (var item in batch.Items)
        {
            var evidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, item.Input.Id, null, actorUserId, ct);
            if (evidence == null) return new(VetPhotoWorkflowStatus.Stale, null, batch, "Источник изменился; нужен новый просмотр.");
            evidenceBySource.Add(item.Source.Id, evidence);
        }
        var identicalGroups = new Dictionary<Guid, Guid>();
        foreach (var group in evidenceBySource.Values.Where(e => e.Original?.State == "retained" && e.Extraction != null
            && e.Candidate.EventId == null && !e.Candidate.ManuallyCorrected && !e.Candidate.RequiresExplicitRestoration
            && e.Candidate.State is not ("linked" or "excluded" or "cancelled" or "deleted"))
            .GroupBy(e => e.Original!.BlobId).Where(g => g.Count() > 1))
        {
            var readings = group.Select(e =>
            {
                var image = VetPhotoInterpretationParser.Parse(e.Extraction!.StructuredJson, e.Source.Id, e.Input.Id);
                var (context, failure) = VetPhotoCaptionContext.Read(e.Caption);
                var valid = image == null || failure is not (null or "caption_not_saved") ? null
                    : VetPhotoValidationRules.Validate(image, context, assumptions, e.Input.ReceivedAt).Effective;
                return (Evidence: e, Reading: valid);
            }).ToArray();
            if (readings.Any(r => r.Reading == null) || readings.Any(r =>
                r.Reading!.Value != readings[0].Reading!.Value || r.Reading.Unit != readings[0].Reading!.Unit
                || r.Reading.OccurredAt != readings[0].Reading!.OccurredAt)) continue;
            if (selectedCandidateIds != null && readings.Any(r => !selectedCandidateIds.Contains(r.Evidence.Candidate.Id))) continue;
            if (readings.Any(r => r.Evidence.Candidate.DuplicateDecision != "unresolved")) continue;
            var canonicalReading = readings.OrderBy(r => r.Evidence.Source.ItemNumber).First();
            var canonical = canonicalReading.Evidence.Candidate.Id;
            var state = EventState(canonicalReading.Evidence, batch.Batch.Id, canonicalReading.Reading!);
            if (!await diary.CanAutoLinkPhotoGroupAsync(scope, profile.Id, canonical,
                readings.Select(r => r.Evidence.Candidate.Id).ToArray(), state, ct)) continue;
            foreach (var reading in readings) identicalGroups[reading.Evidence.Candidate.Id] = canonical;
        }
        foreach (var item in batch.Items)
        {
            var evidence = evidenceBySource[item.Source.Id];
            var number = item.Source.ItemNumber!.Value;
            var candidate = evidence.Candidate;
            var context = ReadContext(candidate);
            var (captionContext, captionFailure) = VetPhotoCaptionContext.Read(evidence.Caption);
            if (!candidate.ManuallyCorrected) context = captionContext;
            var image = evidence.Extraction == null ? null : VetPhotoInterpretationParser.Parse(evidence.Extraction.StructuredJson, item.Source.Id, item.Input.Id);
            var validation = image == null ? new VetPhotoValidation(null, ["image_result_unavailable"])
                : VetPhotoValidationRules.Validate(image, context, assumptions, item.Input.ReceivedAt);
            if (captionFailure is "ambiguous_caption_readings" or "uncertain_caption_reading" or "caption_date_requires_clarification" or "caption_processing"
                && !candidate.ManuallyCorrected) validation = new(null, [captionFailure]);
            var details = $"Источник {item.Source.Id:D}; вход {item.Input.Id:D}; кандидат {candidate.Id:D}, версия {candidate.Revision}.";
            var evidenceBlocks = new List<string>(VetPhotoReviewEvidence.Candidate(candidate));
            evidenceBlocksByNumber[number] = evidenceBlocks;
            if (evidence.OwnedEvent is { } currentFact)
                evidenceBlocks.AddRange(VetPhotoReviewEvidence.Data("Текущий сохранённый факт",
                    VetPhotoReviewEvidence.StateText($"Текущий сохранённый факт #{currentFact.Id}, версия {currentFact.Revision}", VetPhotoReviewEvidence.State(currentFact))));
            if (candidate.InputRevisionId is { } oldInput && candidate.ExtractionResultId is { } oldResult
                && (oldInput != evidence.Input.Id || oldResult != evidence.Extraction?.Id))
            {
                var oldEvidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, oldInput, oldResult, actorUserId, ct);
                if (oldEvidence?.Extraction?.Id != oldResult) return new(VetPhotoWorkflowStatus.Stale, null, batch, "Прежнее основание изменилось; нужен новый просмотр.");
                evidenceBlocks.AddRange(VetPhotoReviewEvidence.Image("Прежний дисплей кандидата (данные)", oldEvidence));
            }

            if (captionFailure == "caption_not_saved") details += "\nПодпись не сохранена в TEXT-дневник; её факты здесь не подтверждаются.";
            if (image != null && validation.Effective == null && evidence.Extraction?.State != "comparison")
            {
                foreach (var display in image.Displays)
                    details += $"\nПоказано на новой версии: {display.ValueText}; число {display.NumericValue}; единица {display.Unit ?? "не показана"}; год {display.Year}, показан {display.YearDisplayed}; месяц {display.Month}; день {display.Day}; время {display.Time ?? "не показано"}; смещение {display.Offset ?? "не показано"}.";
                foreach (var reason in image.Reasons) details += "\nНеопределённость: " + reason;
                if (image.Notes != null) details += "\nТекст изображения (данные): " + image.Notes;
            }
            if (evidence.Extraction?.State == "comparison")
            {
                details += $"\nСравнение результата {evidence.Extraction.Id:D}; автор источника {evidence.Source.SourceAuthorUserId}. Защищённый кандидат и факт не меняются.";
                if (image != null)
                {
                    foreach (var display in image.Displays)
                        details += $"\nПоказано на новой версии: {display.ValueText}; число {display.NumericValue}; единица {display.Unit ?? "не показана"}; год {display.Year}, показан {display.YearDisplayed}; месяц {display.Month}; день {display.Day}; время {display.Time ?? "не показано"}; смещение {display.Offset ?? "не показано"}.";
                    foreach (var reason in image.Reasons) details += "\nНеопределённость: " + reason;
                    if (image.Notes != null) details += "\nТекст изображения (данные): " + image.Notes;
                }
                if (evidence.OwnedEvent is { } oldFact)
                    details += $"\nСохранённый факт #{oldFact.Id} rev {oldFact.Revision}: {oldFact.Value.ToString(CultureInfo.InvariantCulture)} {oldFact.Unit}; {oldFact.LocalTime}; {oldFact.TimeZoneSnapshot}; UTC {oldFact.OccurredAt.ToUniversalTime():O}.";
                details += "\nДля изменения укажите точный источник и новое значение/время; исключённые или удалённые данные требуют явного восстановления. Из этого сравнения ничего не сохраняется.";
                rows.Add(new(number, VetPhotoReviewSection.Exceptions, null, details));
                continue;
            }
            if (candidate.State is "linked" or "excluded" or "cancelled" or "deleted" && !explicitRestoration)
            {
                rows.Add(new(number, VetPhotoReviewSection.Excluded, validation.Effective, details + "\n" + StateLabel(candidate.State)));
                continue;
            }
            if (candidate.EventId != null && !candidate.ManuallyCorrected
                && (selectedCandidateIds == null || !selectedCandidateIds.Contains(candidate.Id)))
            {
                var changedInput = evidence.OwnedEvent?.InputRevisionId != evidence.Input.Id;
                rows.Add(new(number, changedInput ? VetPhotoReviewSection.Exceptions : VetPhotoReviewSection.Excluded,
                    validation.Effective, details + $"\nУже сохранено: #{candidate.EventId}; "
                    + (changedInput ? "новая версия — отдельное предложение исправления, факт пока не изменён." : "новое сохранение не предлагается.")));
                continue;
            }
            if (validation.Effective == null || evidence.Extraction == null)
            {
                rows.Add(new(number, candidate.State is "failed" or "waiting" ? VetPhotoReviewSection.Failed : VetPhotoReviewSection.Exceptions,
                    null, details + "\n" + string.Join("; ", validation.Reasons)));
                continue;
            }
            var reading = validation.Effective;
            if (evidence.OwnedEvent is { DeletedAt: null } saved && saved.InputRevisionId == evidence.Input.Id
                && saved.ExtractionResultId == evidence.Extraction.Id && saved.Value == reading.Value
                && saved.Unit == reading.Unit && saved.OccurredAt == reading.OccurredAt)
            {
                rows.Add(new(number, VetPhotoReviewSection.Excluded, reading, details + $"\nУже сохранено: #{saved.Id}; изменений нет."));
                continue;
            }
            var state = EventState(evidence, batch.Batch.Id, reading);
            var proof = await diary.GetPhotoCollisionProofAsync(scope, profile.Id, candidate.Id, state, ct);
            if (proof == null) return new(VetPhotoWorkflowStatus.Stale, null, batch, "Повторы изменились; нужен новый просмотр.");
            var decision = candidate.DuplicateDecision == "keep_existing" ? "same" : candidate.DuplicateDecision;
            var selectedByUser = selectedCandidateIds == null || selectedCandidateIds.Contains(candidate.Id);
            var canRestore = !candidate.RequiresExplicitRestoration || explicitRestoration;
            var disposition = candidate.EventId == null ? "save" : "correct";
            long? linkEvent = null; int? linkRevision = null;
            Guid? linkCandidate = null;
            if (identicalGroups.TryGetValue(candidate.Id, out var canonical))
            {
                decision = candidate.Id == canonical ? "canonical" : "same";
                if (candidate.Id != canonical) { disposition = "link"; linkCandidate = canonical; }
                details += "\nОдинаковые исходные байты: один показанный основной факт, остальные источники связаны с ним.";
            }
            if (decision == "same" && candidate.DuplicateEventId != null)
            { disposition = "link"; linkEvent = candidate.DuplicateEventId; linkRevision = candidate.DuplicateEventRevision; }
            if (proof.HasCollisions && decision is not ("separate" or "same" or "canonical"))
            {
                rows.Add(new(number, VetPhotoReviewSection.Duplicates, reading, details + "\nВыберите: то же измерение, отдельное измерение или исключить."));
                continue;
            }
            var included = selectedByUser && canRestore;
            rows.Add(new(number, included ? VetPhotoReviewSection.Clear : VetPhotoReviewSection.Exceptions, reading,
                details + (included ? "\nПредлагается: " + (disposition == "correct" ? $"исправить #{candidate.EventId}" : disposition == "link" ? $"связать с #{linkEvent}" : "сохранить новое измерение")
                    : "\nВ подтверждаемый набор не входит; требуется явный выбор или восстановление.")));
            if (!included) continue;
            selected.Add(new(profile.Id, profile.Revision, batch.Batch.Id, batch.Batch.ReviewRevision, candidate.Id,
                candidate.Revision, item.Source.Id, item.Source.CurrentInputRevisionId, item.Source.CurrentOrdinal,
                item.Input.Id, evidence.Extraction.Id, candidate.ExtractionResultId, evidence.Original?.Id,
                evidence.Original?.Revision, evidence.Original?.State, candidate.EventId, candidate.EventRevision,
                disposition, decision, linkEvent, linkRevision, linkCandidate, explicitRestoration, context, state, proof));
        }
        if (selectedCandidateIds != null && selectedCandidateIds.Any(id => batch.Items.All(i => i.Candidate.Id != id)))
            return new(VetPhotoWorkflowStatus.Refused, null, batch, "Выбранный кандидат не принадлежит этой партии.");
        var status = Status(batch);
        rows = rows.Select(row => row with { EvidenceBlocks = evidenceBlocksByNumber[row.ItemNumber] }).ToList();
        var preview = VetPhotoReviewFormatter.Format($"Партия {batch.Batch.Id:D}\n{status}",
            Assumptions(assumptions), rows, selected.Count == 0 ? "Готового выбранного набора нет. Ничего не сохраняется." :
                $"Подтверждение применит ровно {selected.Count} показанных изменений. Остальные источники останутся без изменения.");
        if (!preview.Success) return new(VetPhotoWorkflowStatus.Refused, null, batch, "Полный просмотр не помещается в допустимые страницы; выберите более короткий точный набор.");
        if (selected.Count == 0)
        {
            if (!durableEvidence) return new(VetPhotoWorkflowStatus.Incomplete, null, batch, status) { Pages = preview.Pages };
            var evidenceSelection = JsonSerializer.Serialize(batch.Items.Select(i => new VetPhotoEvidenceSelection(
                i.Source.Id, i.Source.CurrentInputRevisionId, i.Source.CurrentOrdinal, i.Candidate.Id,
                i.Candidate.Revision, i.Candidate.InputRevisionId, i.Candidate.ExtractionResultId,
                evidenceBySource[i.Source.Id].Extraction?.Id)).ToArray(), Json);
            var noticeKey = StableOperation("evidence:" + batch.Batch.Id + ":" + batch.Batch.ReviewRevision
                + ":" + evidenceSelection + ":" + string.Join("\n", preview.Pages));
            var notice = await workflow.StageReviewAsync(new(scope, noticeKey, actorUserId, VetPhotoReviewKind.Evidence,
                batch.Batch.Id, batch.Batch.ReviewRevision, profile.Revision, evidenceSelection, preview), ct);
            return new(notice.Status, notice.Review, batch, status);
        }
        var selection = JsonSerializer.Serialize(selected, Json);
        var kind = selected.Any(s => s.Disposition == "correct") ? VetPhotoReviewKind.Correction : VetPhotoReviewKind.Save;
        var operation = operationKey ?? StableOperation("batch:" + batch.Batch.Id + ":" + batch.Batch.ReviewRevision + ":" + selection + ":" + string.Join("\n", preview.Pages));
        var stage = await workflow.StageReviewAsync(new(scope, operation, actorUserId, kind, batch.Batch.Id,
            batch.Batch.ReviewRevision, profile.Revision, selection, preview), ct);
        return new(stage.Status, stage.Review, batch, status);
    }

    public async Task<VetPhotoWorkflowStatus> DeliverAsync(VetDiaryScope scope, VetPhotoReview review,
        long actorUserId, ITelegramClient client, int? replyToMessageId, CancellationToken ct, bool explicitRetry = false)
    {
        if (review.State == "preview_failed")
        {
            if (!explicitRetry) return VetPhotoWorkflowStatus.Incomplete;
            var retry = await workflow.RetryPreviewAsync(new(scope, review.Id, review.Revision, review.OperationKey, actorUserId), ct);
            if (retry.Review == null) return retry.Status;
            review = retry.Review;
        }
        if (review.State != "preview") return VetPhotoWorkflowStatus.Stale;
        var handle = new VetPhotoReviewHandle(scope, review.Id, review.Revision, review.OperationKey, actorUserId);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        if (pages.Length is < 1 or > VetPhotoReviewBounds.MaxPages || pages.Any(p => p.Length > 3500)) return VetPhotoWorkflowStatus.Refused;
        var delivered = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        try
        {
            for (var index = 0; index < pages.Length; index++)
            {
                if (delivered.Any(p => p.PageIndex == index)) continue;
                var begun = await workflow.BeginPageDeliveryAsync(handle, index, Hash(pages[index]), ct);
                if (begun.Status == VetPhotoWorkflowStatus.Existing && begun.Review is { } current)
                {
                    delivered = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(current.DeliveredPagesJson, Json)!;
                    if (!delivered.Any(d => d.PageIndex == index && d.MessageId > 0 && d.TextHash == Hash(pages[index])))
                        return VetPhotoWorkflowStatus.Incomplete;
                    continue;
                }
                if (begun.Status != VetPhotoWorkflowStatus.Applied) return begun.Status;
                var messageId = await client.SendTextAsync(scope.ChatId, scope.TopicId, pages[index], replyToMessageId, ct);
                var saved = await workflow.RecordPageDeliveryAsync(handle, index, messageId, Hash(pages[index]), ct);
                if (saved.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
                { await HoldFailedDeliveryAsync(handle); return saved.Status; }
                delivered = delivered.Append(new(index, messageId, Hash(pages[index]))).ToArray();
            }
            var last = delivered.Single(p => p.PageIndex == pages.Length - 1).MessageId;
            var complete = await workflow.CompleteDeliveryAsync(handle, last, ct);
            if (complete.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing)) return complete.Status;
            if (review.Kind == "evidence") return complete.Status;
            var buttons = new InlineButton[] { new("Подтвердить показанный набор", Callback("a", review)), new("Не применять", Callback("d", review)) };
            // Proof already records every full page; a cosmetic button failure cannot grant unseen authority.
            await client.EditMessageButtonsAsync(scope.ChatId, last, buttons, ct);
            return complete.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await HoldFailedDeliveryAsync(handle); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Photo preview delivery deferred: {ExceptionType}", ex.GetType().Name);
            await HoldFailedDeliveryAsync(handle);
            return VetPhotoWorkflowStatus.Incomplete;
        }
    }
    private async Task HoldFailedDeliveryAsync(VetPhotoReviewHandle handle)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await workflow.RecordPreviewFailureAsync(handle, cleanup.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning("Photo preview failure marker deferred: {ExceptionType}", ex.GetType().Name); }
    }

    public static string Callback(string action, VetPhotoReview review) =>
        "vp:" + action + ":" + review.Id.ToString("N") + ":" + review.Revision.ToString(CultureInfo.InvariantCulture);
    public static string Status(VetPhotoBatchSnapshot snapshot)
    {
        var c = snapshot.Counts;
        return $"Принято {c.Admitted}; доставлено {c.Delivered}; оригиналов {c.Retained}; обработано {c.Processed}; " +
            $"готово {c.Clear}; уточнить {c.Pending}; ошибок {c.Failed}; сохранено {c.Saved}; исключено {c.Excluded}; отменено {c.Cancelled}; отклонено {c.Rejected}.";
    }
    private static IReadOnlyList<string> Assumptions(VetPhotoBatchAssumptions a) =>
        ["Единица: " + (a.GlucoseUnit ?? a.ProfileGlucoseUnit ?? "не задана") + (a.UnitConfirmed ? " (подтверждена для партии)" : " (профильное значение по умолчанию)"),
         "Часовой пояс: " + (a.TimeZone ?? a.ProfileTimeZone ?? "не задан") + (a.TimeZoneConfirmed ? " (подтверждён для партии)" : " (профильное значение по умолчанию)"),
         "Год: " + (a.YearConfirmed ? a.Year!.Value.ToString(CultureInfo.InvariantCulture) + " (подтверждён для партии)" : "не подтверждён; по времени загрузки не угадывается")];
    private static string StateLabel(string state) => state switch
    { "linked" => "Связано с существующим фактом.", "excluded" => "Исключено.", "cancelled" => "Остаток отменён.", "deleted" => "Факт удалён; требуется явное восстановление.", _ => state };
    private static VetPhotoContext ReadContext(VetPhotoCandidate candidate)
    {
        try { return JsonSerializer.Deserialize<VetPhotoContext>(candidate.CorrectionProvenanceJson, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private static VetEventState EventState(VetPhotoPresentationEvidence evidence, Guid batchId, VetPhotoEffectiveReading reading) =>
        new("glucose", reading.Value, reading.Unit, null, reading.OccurredAt, reading.LocalTime,
            reading.TimeZoneSnapshot, reading.TimeEvidence, VetPhotoValidationRules.ValueUnitEvidence(reading),
            "photo", evidence.Source.Id, 0, null, evidence.Source.Id, batchId, evidence.Input.Id,
            evidence.Extraction!.Id, evidence.Source.SourceAuthorUserId, evidence.Source.SourceMessageDbId!.Value,
            evidence.Source.TelegramMessageId);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static Guid StableOperation(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
}
