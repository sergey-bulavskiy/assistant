using System.Globalization;
using Assistant.Application.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet.Photos;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet.Photos;

public sealed class VetPhotoApplicationOperations(IVetPhotoArchiveStore archive,
    IVetPhotoWorkflowStore workflow, IVetPhotoPresentationStore presentation, IVetProfileStore profiles,
    IVetPhotoDiaryStore diary, IVetPhotoCommandReceiptStore receipts, VetPhotoReviewComposer composer, VetPhotoDispositionComposer dispositions,
    IVetPhotoRunApplication runs, IVetPhotoReversalApplication reversals, IApprovalService approvals,
    IClock clock, ILogger<VetPhotoApplicationOperations> logger) : IVetPhotoApplicationOperations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string command, string? args, Guid operationKey, CancellationToken ct)
    {
        if (command == "import")
        {
            if (!string.Equals(args?.Trim(), "readings", StringComparison.Ordinal)) return false;
            command = "photos_start"; args = null;
        }
        if (command == "photos_result")
        {
            if (!await AuthorizedAsync(bot, message, ct)) return true;
            var arguments = args?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
            var ids = new List<Guid>();
            if (arguments.Length is < 5 or > 6 || arguments.Length == 6 && arguments[5] != "restore")
            { await SendAsync(client, message, "Нужны пять полных ID: запуск, окно, источник, версия, результат; необязательное restore явно просит восстановление.", ct); return true; }
            foreach (var argument in arguments.Take(5))
            {
                if (!Guid.TryParseExact(argument, "D", out var id) || id == Guid.Empty)
                { await SendAsync(client, message, "Нужны точные полные ID из показанного окна.", ct); return true; }
                ids.Add(id);
            }
            await runs.SelectResultAsync(bot, client, message, ids[0], ids[1], ids[2], ids[3], ids[4], arguments.Length == 6, ct);
            return true;
        }
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["photos_start"] = "start", ["photos_close"] = "close", ["photos"] = "show",
            ["photos_review"] = "review", ["photos_save"] = "save", ["photos_cancel"] = "cancel",
            ["photos_exclude"] = "exclude", ["photos_reprocess"] = "reprocess",
            ["photos_delete_originals"] = "delete_originals", ["photos_continue"] = "continue",
            ["photos_reverse"] = "reverse"
        };
        if (!kinds.TryGetValue(command, out var kind)) return false;
        if (!await AuthorizedAsync(bot, message, ct)) return true;
        var pieces = args?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (kind == "show" && pieces.Length <= 1 && (pieces.Length == 0 || int.TryParse(pieces[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out _)))
        {
            var offset = pieces.Length == 0 ? 0 : int.Parse(pieces[0], CultureInfo.InvariantCulture);
            await HistoryAsync(VetDiaryScope.From(bot, message), message.UserId!.Value, offset, client, message.MessageId, ct);
            return true;
        }
        var operation = Empty(kind);
        if (kind == "start" && pieces.Length != 0 || pieces.Length > 2)
        { await SendAsync(client, message, "Укажите один точный ID партии или запуска; /photos показывает доступные партии.", ct); return true; }
        if (pieces.Length > 0)
        {
            if (kind is "reprocess" or "delete_originals" && pieces[0] is "current" or "all_originals")
            {
                operation = operation with { SelectionMode = pieces[0] };
                if (pieces.Length == 2)
                {
                    if (!Guid.TryParseExact(pieces[1], "D", out var batch))
                    { await SendAsync(client, message, "Нужен полный ID партии.", ct); return true; }
                    operation = operation with { BatchId = batch };
                }
            }
            else if (kind == "reverse" && long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var action) && action > 0)
                operation = operation with { ActionId = action };
            else if (Guid.TryParseExact(pieces[0], "D", out var id))
                operation = kind == "continue" ? operation with { RunId = id } : operation with { BatchId = id };
            else { await SendAsync(client, message, "Нужен полный ID; неизвестный или сокращённый ID не выбирает записи.", ct); return true; }
        }
        if (pieces.Length == 2 && kind is not ("reprocess" or "delete_originals"))
        { await SendAsync(client, message, "Укажите один точный ID. Выбор нескольких строк задайте естественным сообщением с их номерами.", ct); return true; }
        return await OperationAsync(bot, client, message, operation, operationKey, ct);
    }

    public async Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken ct)
    {
        if (!await AuthorizedAsync(bot, message, ct)) return true;
        var scope = VetDiaryScope.From(bot, message); var actor = message.UserId!.Value;
        if (operation.Kind is "start" or "close" or "assumptions" or "add_late" or "correct" or "duplicate" or "cancel")
        {
            var prior = await receipts.ReadPlanAsync(scope, operationKey, actor, ct);
            if (prior.Plan != null)
            {
                if (prior.Plan.RequestJson != JsonSerializer.Serialize(operation, Json))
                    await SendAsync(client, message, "Сохранённая операция имеет другое содержание; ничего не изменено.", ct);
                else await ExecuteFrozenAsync(scope, operation, prior.Plan, client, message, ct);
                return true;
            }
            if (prior.Status != VetPhotoWorkflowStatus.NotFound)
            { await SendAsync(client, message, "Нет действительного сохранённого входа для этой операции.", ct); return true; }
        }
        if (operation.Kind == "start")
        {
            await PrepareCommandAsync(scope, operation, operationKey, actor,
                [new("start")], client, message, ct);
            return true;
        }
        if (operation.Kind is "accept" or "decline")
        { await NaturalDecisionAsync(scope, operation, actor, client, message, ct); return true; }
        if (operation.Kind == "undo")
        {
            var undone = await diary.UndoAsync(scope, actor, operationKey, ct);
            await SendAsync(client, message, Outcome(undone), ct); return true;
        }
        if (operation.Kind == "reverse")
        { await reversals.StageAsync(bot, client, message, operation, operationKey, ct); return true; }
        if (operation.Kind is "reprocess" or "delete_originals")
        { await runs.BeginAsync(bot, client, message, operation, operationKey, ct); return true; }
        if (operation.Kind == "continue")
        {
            if (operation.RunId is { } run) await runs.ContinueAsync(bot, client, message, run, ct);
            else await SendAsync(client, message, "Укажите точный ID запуска; продолжение означает новый подтверждённый объём вызовов.", ct);
            return true;
        }
        if (operation.RunId is { } targetedRun && operation.Kind is "show" or "cancel")
        {
            if (operation.Kind == "show") await runs.ShowAsync(bot, client, message, targetedRun, ct);
            else await runs.CancelAsync(bot, client, message, targetedRun, ct);
            return true;
        }
        if (operation.Kind == "show" && operation.BatchId == null && operation.SourceIds.Count == 0
            && operation.CandidateIds.Count == 0 && message.ReplyToMessageId == null)
        { await HistoryAsync(scope, actor, 0, client, message.MessageId, ct); return true; }
        var batch = await ResolveBatchAsync(scope, operation, actor, message.ReplyToMessageId, ct);
        if (batch == null)
        { await SendAsync(client, message, "Нужна одна точная партия в этом месте. /photos показывает её ID; можно ответить на исходное фото.", ct); return true; }
        if (operation.Kind == "close")
        {
            await PrepareCommandAsync(scope, operation, operationKey, actor,
                [new("close", batch.Batch.Id, batch.Batch.ReviewRevision)], client, message, ct);
            return true;
        }
        if (operation.Kind == "assumptions")
        {
            if (operation.Assumptions == null)
            { await SendAsync(client, message, "Укажите год, единицу или часовой пояс для этой партии.", ct); return true; }
            var changes = new List<(VetPhotoAssumptionKind Kind, string Value)>();
            if (operation.Assumptions.Year is { } year) changes.Add((VetPhotoAssumptionKind.Year, year.ToString(CultureInfo.InvariantCulture)));
            if (operation.Assumptions.TimeZone is { } zone) changes.Add((VetPhotoAssumptionKind.TimeZone, zone));
            if (operation.Assumptions.GlucoseUnit is { } unit) changes.Add((VetPhotoAssumptionKind.Unit, unit));
            if (changes.Count == 0)
            { await SendAsync(client, message, "Укажите хотя бы одно точное допущение.", ct); return true; }
            var profile = await profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, ct);
            var steps = changes.Select((change, index) => new VetPhotoCommandStep("assumption",
                Assumption: new(scope, batch.Batch.Id, checked(batch.Batch.ReviewRevision + index),
                    profile.Revision, actor, change.Kind, change.Value))).ToArray();
            await PrepareCommandAsync(scope, operation, operationKey, actor, steps, client, message, ct);
            return true;
        }
        if (operation.Kind == "add_late")
        {
            if (operation.SourceIds.Count != 1)
            { await SendAsync(client, message, "Для добавления нужен один точный ID позднего источника и открытой партии.", ct); return true; }
            var late = await archive.GetSourceAsync(scope, operation.SourceIds[0], ct);
            if (late?.Source == null || late.Input == null)
            { await SendAsync(client, message, "Поздний источник не найден в этом месте.", ct); return true; }
            await PrepareCommandAsync(scope, operation, operationKey, actor,
                [new("late", Late: new(batch.Batch.Id, batch.Batch.ReviewRevision,
                    late.Source.Id, late.Input.Id))], client, message, ct);
            return true;
        }
        var selected = SelectItems(batch, operation);
        if (selected == null)
        { await SendAsync(client, message, "Укажите существующие номера или точные ID этой партии без повторов.", ct); return true; }
        if (operation.Kind == "correct")
        {
            var corrections = operation.Corrections;
            if (corrections.Count == 0 || corrections.Any(c => c.SourceId == null && selected.Count != 1)
                || corrections.Any(c => c.SourceId != null && selected.Count(i => i.Source.Id == c.SourceId) != 1)
                || corrections.Select(c => c.SourceId ?? selected[0].Source.Id).Distinct().Count() != corrections.Count)
            { await SendAsync(client, message, "Исправление неоднозначно. Укажите один источник для каждого изменения; предложение пока сохранено без изменений.", ct); return true; }
            var targets = corrections.Select(c => (Correction: c, Item: selected.Single(i => i.Source.Id == (c.SourceId ?? selected[0].Source.Id)))).ToArray();
            if (targets.Any(t => t.Correction.TargetCandidateRevision is { } revision && revision != t.Item.Candidate.Revision))
            { await SendAsync(client, message, "Исправление относится к устаревшей версии; нужен новый просмотр.", ct); return true; }
            var steps = new List<VetPhotoCommandStep>();
            foreach (var target in targets)
            {
                var item = batch.Items.Single(i => i.Source.Id == target.Item.Source.Id);
                var evidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, item.Input.Id, null, actor, ct);
                if (evidence == null) { await SendAsync(client, message, "Источник изменился; исправление не применено.", ct); return true; }
                var change = target.Correction;
                var date = change.Date == null ? (DateOnly?)null : DateOnly.ParseExact(change.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var onlySavedValue = evidence.OwnedEvent != null && change.Date == null && change.Time == null && change.Offset == null;
                var (caption, _) = VetPhotoCaptionContext.Read(evidence.Caption);
                var context = new VetPhotoContext(change.Value, change.Unit ?? caption.Unit ?? evidence.OwnedEvent?.Unit,
                    date?.Year ?? (onlySavedValue ? null : caption.Year), date?.Month ?? (onlySavedValue ? null : caption.Month),
                    date?.Day ?? (onlySavedValue ? null : caption.Day), change.Time ?? (onlySavedValue ? null : caption.Time),
                    change.Offset ?? (onlySavedValue ? null : caption.Offset), CorrectionApproved: true);
                steps.Add(new("human", Human: new(scope, batch.Batch.Id,
                    checked(batch.Batch.ReviewRevision + steps.Count), item.Candidate.Id,
                    item.Candidate.Revision, item.Input.Id, item.Source.CurrentOrdinal, actor,
                    context, change.RestoreRequested)));
            }
            await PrepareCommandAsync(scope, operation, operationKey, actor, steps, client, message, ct);
            return true;
        }
        if (operation.Kind == "duplicate")
        {
            if (selected.Count != 1 || operation.DuplicateChoice is not ("same" or "separate" or "exclude"))
            { await SendAsync(client, message, "Выберите один точный источник: то же измерение, отдельное или исключить.", ct); return true; }
            if (operation.DuplicateChoice == "exclude")
            { await ShowDispositionAsync(scope, batch, actor, selected, "exclude", client, message.MessageId, ct); return true; }
            var item = selected[0];
            var existing = operation.TargetEventId == null ? null : await diary.GetEventAsync(scope, operation.TargetEventId.Value, ct);
            if (operation.DuplicateChoice == "same" && existing == null)
            { await SendAsync(client, message, "Укажите ID существующего факта в этом месте для связи.", ct); return true; }
            var change = new VetPhotoCandidateChange(scope, batch.Batch.Id, item.Candidate.Id,
                batch.Batch.ReviewRevision, item.Candidate.Revision, item.Input.Id, item.Source.CurrentOrdinal,
                item.Candidate.ExtractionResultId, actor, operation.DuplicateChoice == "same"
                    ? VetPhotoCandidateChangeKind.DuplicateExisting : VetPhotoCandidateChangeKind.DuplicateSeparate,
                DuplicateEventId: existing?.Id, DuplicateEventRevision: existing?.Revision);
            await PrepareCommandAsync(scope, operation, operationKey, actor,
                [new("duplicate", Candidate: change)], client, message, ct);
            return true;
        }
        if (operation.Kind is "exclude" or "cancel")
        {
            if (operation.Kind == "cancel" && batch.Batch.State == "collecting")
            {
                await PrepareCommandAsync(scope, operation, operationKey, actor,
                    [new("close", batch.Batch.Id, batch.Batch.ReviewRevision)], client, message, ct);
                return true;
            }
            await ShowDispositionAsync(scope, batch, actor, selected, operation.Kind, client, message.MessageId, ct); return true;
        }
        if (operation.Kind is "review" or "show" or "save")
        {
            var hasSelection = operation.SourceIds.Count + operation.CandidateIds.Count + operation.ItemNumbers.Count > 0;
            await ShowReviewAsync(scope, batch.Batch.Id, actor, hasSelection ? selected.Select(i => i.Candidate.Id).ToArray() : null,
                false, client, message.MessageId, ct, explicitRetry: true); return true;
        }
        await SendAsync(client, message, "Операция требует точного просмотра партии.", ct); return true;
    }

    private async Task PrepareCommandAsync(VetDiaryScope scope, VetPhotoOperation operation,
        Guid key, long actor, IReadOnlyList<VetPhotoCommandStep> steps, ITelegramClient client,
        IncomingMessage message, CancellationToken ct)
    {
        var prepared = await receipts.PreparePlanAsync(new(scope, key, actor,
            JsonSerializer.Serialize(operation, Json), steps), ct);
        if (prepared.Plan == null)
        { await SendAsync(client, message, "Операция не подготовлена: вход, область или выбранные версии изменились.", ct); return; }
        await ExecuteFrozenAsync(scope, operation, prepared.Plan, client, message, ct);
    }

    private async Task ExecuteFrozenAsync(VetDiaryScope scope, VetPhotoOperation operation,
        VetPhotoCommandPlan plan, ITelegramClient client, IncomingMessage message, CancellationToken ct)
    {
        VetPhotoCommandStepOutcome? last = null;
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            last = await receipts.ExecuteStepAsync(scope, plan.SourceOperationKey, index, plan.ActorUserId, ct);
            if (last.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
            { await SendAsync(client, message, "Операция удержана: выбранная версия устарела или не разрешена. Уже выполненные части сохранены; новые цели не подставляются.", ct); return; }
        }
        if (last?.BatchId is not { } batchId) return;
        if (operation.Kind == "start")
        { await SendAsync(client, message, $"Приём открыт: {batchId:D}. До 50 фото; завершите явно /photos_close. Время загрузки не становится временем измерения.", ct); return; }
        if (operation.Kind == "add_late")
        { await SendAsync(client, message, "Источник явно добавлен в открытую партию.", ct); return; }
        var reviewKey = ChildKey(plan.SourceOperationKey, "review");
        if (operation.Kind == "cancel")
        {
            var batch = await workflow.GetBatchAsync(scope, batchId, plan.ActorUserId, ct);
            var selected = batch == null ? null : SelectItems(batch, operation);
            if (batch == null || selected == null) return;
            await ShowDispositionAsync(scope, batch, plan.ActorUserId, selected, "cancel", client,
                message.MessageId, ct, reviewKey); return;
        }
        var ids = plan.Steps.Select(step => step.Human?.CandidateId ?? step.Candidate?.CandidateId)
            .Where(id => id != null).Select(id => id!.Value).ToArray();
        await ShowReviewAsync(scope, batchId, plan.ActorUserId, ids.Length == 0 ? null : ids,
            plan.Steps.Any(step => step.Human?.RestoreRequested == true), client, message.MessageId, ct, reviewKey);
    }

    private static Guid ChildKey(Guid key, string purpose) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes("photo-operation:" + key.ToString("D") + ":" + purpose)).AsSpan(0, 16));

    public async Task<bool> TryNaturalDecisionAsync(ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetOperation operation, CancellationToken ct)
    {
        if (operation.Kind is not ("accept" or "decline") || operation.PendingId != null
            || !await AuthorizedAsync(bot, message, ct)) return false;
        var scope = VetDiaryScope.From(bot, message); var actor = message.UserId!.Value;
        var found = await workflow.FindNaturalReviewAsync(scope, actor, null, operation.ReviewRevision, ct);
        if (found.Status is VetPhotoWorkflowStatus.NotFound or VetPhotoWorkflowStatus.Refused) return false;
        var pending = (await diary.GetPendingAsync(scope, ct)).Where(p => p.State == "pending"
            && p.ExpiresAt > clock.UtcNow && p.PromptMessageId != null).ToArray();
        if (pending.Any(p => p.PromptMessageId == message.ReplyToMessageId)) return false;
        if (found.Review == null || pending.Length > 0 && message.ReplyToMessageId != found.Review.AcceptancePromptMessageId)
        { await SendAsync(client, message, "Есть несколько предложений. Укажите точный ID и версию либо ответьте на последнее сообщение нужного полного просмотра.", ct); return true; }
        if (message.ReplyToMessageId is { } reply && reply != found.Review.AcceptancePromptMessageId
            || found.Status != VetPhotoWorkflowStatus.Existing)
        { await SendAsync(client, message, "Нужен один полный актуальный просмотр в этом месте.", ct); return true; }
        await DecisionAsync(scope, found.Review, operation.Kind == "accept", actor, null, client, message.MessageId, ct);
        return true;
    }

    public async Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct)
    {
        if (!callback.Data.StartsWith("vp:", StringComparison.Ordinal)) return false;
        var parts = callback.Data.Split(':');
        if (bot.FamilyId == null || callback.MessageId <= 0 || callback.MessageChatId == 0
            || callback.MessageChatType is not ("private" or "group" or "supergroup") || parts.Length != 4
            || parts[1] is not ("a" or "d") || !Guid.TryParseExact(parts[2], "N", out var id)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
        { await client.AnswerCallbackAsync(callback.CallbackQueryId, "Просмотр недействителен.", ct); return true; }
        var message = new IncomingMessage(callback.MessageChatId, callback.MessageChatType, null, callback.MessageTopicId,
            callback.MessageId, callback.FromUserId, null, null, MessageKind.Text, false, clock.UtcNow, null, null, "{}", null, null);
        if (!await AuthorizedAsync(bot, message, ct))
        { await client.AnswerCallbackAsync(callback.CallbackQueryId, "Нет доступа к этому месту.", ct); return true; }
        var scope = VetDiaryScope.From(bot, message);
        var review = await presentation.ReadPreviewAsync(scope, id, callback.FromUserId, ct);
        if (review == null || review.Revision != revision || review.AcceptancePromptMessageId != callback.MessageId)
        { await client.AnswerCallbackAsync(callback.CallbackQueryId, "Этот просмотр устарел.", ct); return true; }
        await DecisionAsync(scope, review, parts[1] == "a", callback.FromUserId, callback.MessageId, client, callback.MessageId, ct);
        await client.AnswerCallbackAsync(callback.CallbackQueryId, null, ct);
        var finalReview = await presentation.ReadPreviewAsync(scope, review.Id, callback.FromUserId, ct);
        if (finalReview?.State is "accepted" or "declined")
        {
            try { await client.EditMessageButtonsAsync(scope.ChatId, callback.MessageId, [], ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning("Photo buttons removal deferred: {ExceptionType}", ex.GetType().Name); }
        }
        return true;
    }

    private async Task NaturalDecisionAsync(VetDiaryScope scope, VetPhotoOperation operation, long actor,
        ITelegramClient client, IncomingMessage message, CancellationToken ct)
    {
        VetPhotoReview? review;
        if (operation.ReviewId is { } id)
        {
            review = await presentation.ReadPreviewAsync(scope, id, actor, ct);
            if (operation.ReviewRevision == null || review?.Revision != operation.ReviewRevision) review = null;
        }
        else
        {
            var found = await workflow.FindNaturalReviewAsync(scope, actor, null, operation.ReviewRevision, ct);
            review = found.Status == VetPhotoWorkflowStatus.Existing ? found.Review : null;
        }
        if (review == null || message.ReplyToMessageId is { } reply && reply != review.AcceptancePromptMessageId)
        { await SendAsync(client, message, "Нужен один полный актуальный просмотр. Ответьте на его последнее сообщение или укажите ID и версию.", ct); return; }
        await DecisionAsync(scope, review, operation.Kind == "accept", actor, null, client, message.MessageId, ct);
    }

    private async Task DecisionAsync(VetDiaryScope scope, VetPhotoReview review, bool accept, long actor,
        int? prompt, ITelegramClient client, int? replyTo, CancellationToken ct)
    {
        if (review.Kind == "evidence")
        { await client.SendTextAsync(scope.ChatId, scope.TopicId, "Read-only evidence; no facts or actions are accepted here.", replyTo, ct); return; }
        if (!accept)
        {
            var declined = await presentation.DeclineReviewAsync(new(scope, review.Id, review.Revision, review.OperationKey, actor, prompt), ct);
            await client.SendTextAsync(scope.ChatId, scope.TopicId, declined == VetPhotoWorkflowStatus.Applied
                ? "Показанный набор не применён; уже сохранённые факты и оригиналы остаются." : "Просмотр устарел или показан не полностью.", replyTo, ct);
            return;
        }
        if (await runs.ConfirmAsync(scope, review, actor, prompt, client, replyTo, ct)
            || await reversals.ConfirmAsync(scope, review, actor, prompt, client, replyTo, ct)) return;
        var result = await diary.ApplyPhotoReviewAsync(new(scope, review.Id, review.Revision, review.OperationKey, actor, prompt), ct);
        await client.SendTextAsync(scope.ChatId, scope.TopicId, Outcome(result), replyTo, ct);
    }

    private async Task<VetPhotoBatchSnapshot?> ResolveBatchAsync(VetDiaryScope scope, VetPhotoOperation op,
        long actor, int? replyTo, CancellationToken ct)
    {
        if (op.BatchId is { } id) return await workflow.GetBatchAsync(scope, id, actor, ct);
        var sourceIds = op.SourceIds.Concat(op.Corrections.Where(c => c.SourceId != null).Select(c => c.SourceId!.Value)).Distinct().ToArray();
        var batches = new HashSet<Guid>();
        foreach (var sourceId in sourceIds)
        {
            var source = await archive.GetSourceAsync(scope, sourceId, ct);
            if (source?.Source?.BatchId is not { } sourceBatch) return null;
            batches.Add(sourceBatch);
        }
        if (replyTo is { } telegramId && (await archive.FindSourceAsync(scope, telegramId, ct))?.Source?.BatchId is { } repliedBatch)
            batches.Add(repliedBatch);
        if (batches.Count == 1) return await workflow.GetBatchAsync(scope, batches.Single(), actor, ct);
        if (batches.Count > 1) return null;
        var history = await workflow.ListBatchesAsync(scope, actor, 0, 50, ct);
        var eligible = history.Batches.Where(b => op.Kind == "close" ? b.Batch.State == "collecting"
            : b.Batch.State is "collecting" or "closed").ToArray();
        return eligible.Length == 1 && history.NextOffset == null
            ? await workflow.GetBatchAsync(scope, eligible[0].Batch.Id, actor, ct) : null;
    }

    private static IReadOnlyList<VetPhotoBatchItem>? SelectItems(VetPhotoBatchSnapshot batch, VetPhotoOperation op)
    {
        if (op.SourceIds.Any(id => !batch.Items.Any(i => i.Source.Id == id))
            || op.CandidateIds.Any(id => !batch.Items.Any(i => i.Candidate.Id == id))
            || op.ItemNumbers.Any(n => !batch.Items.Any(i => i.Source.ItemNumber == n))) return null;
        if (op.SourceIds.Count + op.CandidateIds.Count + op.ItemNumbers.Count == 0) return batch.Items;
        return batch.Items.Where(i => op.SourceIds.Contains(i.Source.Id) || op.CandidateIds.Contains(i.Candidate.Id)
            || i.Source.ItemNumber is { } n && op.ItemNumbers.Contains(n)).ToArray();
    }

    private async Task ShowReviewAsync(VetDiaryScope scope, Guid batchId, long actor, IReadOnlyList<Guid>? selected,
        bool restore, ITelegramClient client, int? replyTo, CancellationToken ct, Guid? operationKey = null, bool explicitRetry = false)
    {
        var built = await composer.BuildBatchAsync(scope, batchId, actor, selected, restore, ct, operationKey, durableEvidence: true);
        if (built.Review != null) await composer.DeliverAsync(scope, built.Review, actor, client, replyTo, ct, explicitRetry: explicitRetry);
        else if (built.Pages.Count != 0)
            foreach (var page in built.Pages) await client.SendTextAsync(scope.ChatId, scope.TopicId, page, replyTo, ct);
        else await client.SendTextAsync(scope.ChatId, scope.TopicId, built.StatusText, replyTo, ct);
    }

    private async Task ShowDispositionAsync(VetDiaryScope scope, VetPhotoBatchSnapshot batch, long actor,
        IReadOnlyList<VetPhotoBatchItem> selected, string disposition, ITelegramClient client, int? replyTo, CancellationToken ct, Guid? operationKey = null)
    {
        var built = await dispositions.BuildAsync(scope, batch.Batch.Id, actor,
            selected.Select(i => i.Candidate.Id).ToArray(), disposition, ct, operationKey);
        if (built.Review != null) await composer.DeliverAsync(scope, built.Review, actor, client, replyTo, ct);
        else if (built.Pages.Count != 0)
            foreach (var page in built.Pages) await client.SendTextAsync(scope.ChatId, scope.TopicId, page, replyTo, ct);
        else await client.SendTextAsync(scope.ChatId, scope.TopicId, built.StatusText, replyTo, ct);
    }

    private async Task HistoryAsync(VetDiaryScope scope, long actor, int offset, ITelegramClient client, int? replyTo, CancellationToken ct)
    {
        if (offset < 0) return;
        var page = await workflow.ListBatchesAsync(scope, actor, offset, 10, ct);
        var text = "Партии в этом месте:\n" + string.Join('\n', page.Batches.Select(b =>
            $"{b.Batch.Id:D}; {b.Batch.State}; принято {b.Counts.Admitted}, сохранено {b.Counts.Saved}, уточнить {b.Counts.Pending}, ошибок {b.Counts.Failed}."));
        if (page.NextOffset is { } next) text += "\nСледующая страница: /photos " + next.ToString(CultureInfo.InvariantCulture);
        await client.SendTextAsync(scope.ChatId, scope.TopicId, text, replyTo, ct);
    }

    public Task ResumeRunsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct) => runs.ResumeAsync(bot, client, ct);
    public Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work,
        VetPhotoProcessResult result, CancellationToken ct) => runs.WorkFinishedAsync(bot, client, work, result, ct);
    private async Task<bool> AuthorizedAsync(ReceivingBot bot, IncomingMessage message, CancellationToken ct) =>
        BotRoles.IsVet(bot.Role) && bot.FamilyId is { } family && message.UserId is { } actor
        && await approvals.FindFamilyMemberStatusAsync(family, actor, ct) == FamilyMemberStatus.Approved
        && (message.ChatType == "private" && message.ChatId == actor && message.TopicId == null
            || message.ChatType is "group" or "supergroup"
                && await approvals.FindPlaceStatusAsync(bot.BotDbId, message.ChatId, message.TopicId, ct) == PlaceStatus.Approved);
    private static Task<int> SendAsync(ITelegramClient client, IncomingMessage message, string text, CancellationToken ct) =>
        client.SendTextAsync(message.ChatId, message.TopicId, text, message.MessageId, ct);
    private static string Outcome(VetMutationResult result) => result.Status switch
    {
        VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied => "Подтверждённый набор применён. Факты: "
            + string.Join(", ", result.EventIds.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture)))
            + (result.ProtectedIds.Count == 0 ? "." : ". Более поздние изменения защищены: " + string.Join(", ", result.ProtectedIds)),
        VetMutationStatus.NoChange => "Изменений нет; факты остаются прежними.",
        _ => "Ничего не применено: просмотр устарел, показан не полностью или не относится к этому месту. Нужен новый просмотр."
    };
    private static VetPhotoOperation Empty(string kind) => new(kind, null, null, null, [], [], null, [],
        null, null, null, null, null, null, null, null, []);
}
