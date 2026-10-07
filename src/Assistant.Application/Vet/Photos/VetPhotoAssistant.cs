using System.Globalization;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet.Photos;

public sealed class VetPhotoAssistant(IVetPhotoArchiveStore archive, IVetPhotoBindingStore binding,
    IVetPhotoWorkflowStore workflow, IVetPhotoPresentationStore presentation, VetPhotoReviewComposer composer,
    IVetPhotoApplicationOperations operations, IApprovalService approvals, ILogger<VetPhotoAssistant> logger) : IVetPhotoAssistant
{
    public async Task<VetPhotoAdmission?> AdmitAsync(ReceivingBot bot, IncomingMessage message, long updateId,
        VetAdmittedSource? textAdmission, CancellationToken ct)
    {
        if (!BotRoles.IsVet(bot.Role) || message.Kind is not (MessageKind.Photo or MessageKind.Document)
            || !await AuthorizedAsync(bot, message, ct)) return null;
        var attachment = SelectAttachment(message);
        if (attachment == null) return new(VetPhotoAdmissionStatus.InvalidMetadata, null, null);
        return await archive.AdmitAsync(VetDiaryScope.From(bot, message), message, updateId,
            attachment, textAdmission?.Revision.Id, ct);
    }

    public async Task BindAsync(ReceivingBot bot, IncomingMessage message, StoreResult stored,
        VetPhotoAdmission? admission, CancellationToken ct)
    {
        if (admission?.Source is not { } source || !await AuthorizedAsync(bot, message, ct)) return;
        var scope = VetDiaryScope.From(bot, message);
        if (stored.MessageDbId is { } messageId)
        {
            // Match every immutable transport field before accepting even a returned DB identifier.
            await binding.BindStoredMessageAsync(scope, source.Id, message.UserId!.Value, ct);
        }
        else if (stored.Outcome is StoreOutcome.AlreadyProcessed or StoreOutcome.Duplicate)
            await binding.BindStoredMessageAsync(scope, source.Id, message.UserId!.Value, ct);
    }

    public async Task HandleAdmissionAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        StoreResult stored, VetPhotoAdmission? admission, CancellationToken ct)
    {
        if (admission == null || !await AuthorizedAsync(bot, message, ct)) return;
        var scope = VetDiaryScope.From(bot, message);
        if (admission.Source is { } admittedSource && admission.Status is VetPhotoAdmissionStatus.Admitted or VetPhotoAdmissionStatus.Existing)
        {
            var bound = await archive.GetSourceAsync(scope, admittedSource.Id, ct);
            if (bound?.Source?.SourceMessageDbId == null)
            {
                await client.SendTextAsync(scope.ChatId, scope.TopicId,
                    "Исходное фото зарегистрировано, но связь с сообщением не подтверждена. Обработка не начата; сохранённых фактов нет.", message.MessageId, ct);
                return;
            }
        }
        var text = admission.Status switch
        {
            VetPhotoAdmissionStatus.InvalidMetadata => "Нужен оригинал JPEG или PNG до 10 МиБ; вложение не принято.",
            VetPhotoAdmissionStatus.Full => "В партии уже 50 фото. Это фото не добавлено; завершите партию и начните следующую.",
            VetPhotoAdmissionStatus.Late => "Приём этой партии уже завершён. Фото сохранено как поздний источник; явно добавьте его в открытую партию или начните новую.",
            VetPhotoAdmissionStatus.Refused => "Фото не принято: проверьте доступ к этому месту.",
            _ => null
        };
        if (text != null)
        {
            if (admission.Source != null) text += "\nИсточник: " + admission.Source.Id.ToString("D");
            await client.SendTextAsync(scope.ChatId, scope.TopicId, text, message.MessageId, ct);
            return;
        }
        if (admission.Source?.BatchId is { } batchId)
            await ProgressAsync(scope, batchId, message.UserId!.Value, client, message.MessageId, ct);
    }

    public async Task<VetInterpretation> FilterCaptionAsync(VetDiaryScope scope, int telegramMessageId,
        VetInterpretation interpretation, CancellationToken ct)
    {
        var photo = await archive.FindSourceAsync(scope, telegramMessageId, ct);
        return photo?.Source != null ? interpretation with
        { Events = interpretation.Events.Where(e => e.EventType != "glucose").ToArray() } : interpretation;
    }

    public async Task<string> DescribeAsync(VetDiaryScope scope, long actorUserId, CancellationToken ct)
    {
        var history = await workflow.ListBatchesAsync(scope, actorUserId, 0, 5, ct);
        var lines = new List<string>();
        foreach (var summary in history.Batches)
        {
            lines.Add($"Batch {summary.Batch.Id:D}; revision {summary.Batch.ReviewRevision}; state {summary.Batch.State}");
            var snapshot = await workflow.GetBatchAsync(scope, summary.Batch.Id, actorUserId, ct);
            if (snapshot == null) continue;
            foreach (var item in snapshot.Items.Take(10))
                lines.Add($"Item {item.Source.ItemNumber}; source {item.Source.Id:D}; candidate {item.Candidate.Id:D}; candidate_revision {item.Candidate.Revision}; state {item.Candidate.State}");
        }
        var review = await workflow.FindNaturalReviewAsync(scope, actorUserId, null, null, ct);
        if (review.Review is { } current)
            lines.Add($"Photo review {current.Id:D}; review_revision {current.Revision}; full_shown {current.CompletePreviewDelivered}; prompt {current.AcceptancePromptMessageId}");
        else if (review.Status == VetPhotoWorkflowStatus.Ambiguous)
            lines.Add("Multiple current photo reviews; exact review ID/revision or acceptance button required.");
        return string.Join('\n', lines);
    }

    public async Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (!BotRoles.IsVet(bot.Role) || bot.FamilyId is not { } familyId) return;
        foreach (var admission in await archive.GetUnboundAsync(familyId, bot.BotDbId, 5, ct))
        {
            if (admission.Source is not { } source) continue;
            var scope = new VetDiaryScope(familyId, bot.BotDbId, bot.TelegramBotId, source.ChatId, source.TopicId);
            await binding.BindStoredMessageAsync(scope, source.Id, source.SourceAuthorUserId, ct);
        }
        await operations.ResumeRunsAsync(bot, client, ct);
        foreach (var batch in await presentation.GetRecoverableBatchesAsync(familyId, bot.BotDbId, 5, ct))
        {
            await ProgressAsync(batch.Scope, batch.BatchId, batch.ActorUserId, client, null, ct);
            var result = await composer.BuildBatchAsync(batch.Scope, batch.BatchId, batch.ActorUserId, null, false, ct, durableEvidence: true);
            if (result.Review != null)
                await composer.DeliverAsync(batch.Scope, result.Review, batch.ActorUserId, client, null, ct);
        }
    }

    public async Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work,
        VetPhotoProcessResult result, CancellationToken ct)
    {
        if (bot.FamilyId != work.Scope.FamilyId || bot.BotDbId != work.Scope.BotDbId
            || bot.TelegramBotId != work.Scope.TelegramBotId) return;
        await operations.WorkFinishedAsync(bot, client, work, result, ct);
        var source = await archive.GetSourceAsync(work.Scope, work.SourceId, ct);
        if (source?.Source?.BatchId is { } batchId)
        {
            await ProgressAsync(work.Scope, batchId, work.ActorUserId, client, null, ct);
            if (!work.ArchiveOnly && result.Status == VetPhotoProcessStatus.Review)
            {
                var built = await composer.BuildBatchAsync(work.Scope, batchId, work.ActorUserId, null, false, ct, durableEvidence: true);
                if (built.Review != null) await composer.DeliverAsync(work.Scope, built.Review, work.ActorUserId, client, null, ct);
            }
        }
    }

    public Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string command, string? args, Guid operationKey, CancellationToken ct) =>
        operations.CommandAsync(bot, client, message, command, args, operationKey, ct);
    public Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken ct) =>
        operations.OperationAsync(bot, client, message, operation, operationKey, ct);
    public Task<bool> TryNaturalDecisionAsync(ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetOperation operation, CancellationToken ct) =>
        operations.TryNaturalDecisionAsync(bot, client, message, operation, ct);
    public Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client,
        CallbackQueryInfo callback, CancellationToken ct) => operations.HandleCallbackAsync(bot, client, callback, ct);

    private async Task ProgressAsync(VetDiaryScope scope, Guid batchId, long actor, ITelegramClient client,
        int? replyToMessageId, CancellationToken ct)
    {
        var batch = await workflow.GetBatchAsync(scope, batchId, actor, ct);
        if (batch == null) return;
        var text = "Партия " + batchId.ToString("D") + "; " + batch.Batch.State + ".\n" + VetPhotoReviewComposer.Status(batch);
        if (batch.Batch.State == "collecting") text += "\nЗавершите приём явно: /photos_close.";
        if (batch.Batch.ProgressMessageId is { } progress)
        {
            try { await client.EditMessageTextAsync(scope.ChatId, progress, text, ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning("Photo progress edit deferred: {ExceptionType}", ex.GetType().Name); return; }
        }
        var sent = await client.SendTextAsync(scope.ChatId, scope.TopicId, text, replyToMessageId, ct);
        await presentation.SetProgressMessageAsync(scope, batchId, actor, sent, ct);
    }

    private async Task<bool> AuthorizedAsync(ReceivingBot bot, IncomingMessage message, CancellationToken ct) =>
        bot.FamilyId is { } familyId && message.UserId is { } actor
        && await approvals.FindFamilyMemberStatusAsync(familyId, actor, ct) == FamilyMemberStatus.Approved
        && (message.ChatType == "private" && message.ChatId == actor && message.TopicId == null
            || message.ChatType is "group" or "supergroup"
                && await approvals.FindPlaceStatusAsync(bot.BotDbId, message.ChatId, message.TopicId, ct) == PlaceStatus.Approved);

    internal static VetPhotoAttachment? SelectAttachment(IncomingMessage message)
    {
        if (message.Photo is { Sizes.Count: > 0 } photo)
        {
            var largest = photo.Sizes.Where(s => s.Width > 0 && s.Height > 0 && s.FileId.Length > 0)
                .OrderByDescending(s => (long)s.Width * s.Height).ThenByDescending(s => s.FileSize ?? -1)
                .ThenBy(s => s.FileId, StringComparer.Ordinal).FirstOrDefault();
            return largest == null ? null : new(largest.FileId, largest.FileUniqueId, null, null,
                largest.FileSize, largest.Width, largest.Height);
        }
        if (message.Document is { } document && document.FileId.Length > 0
            && document.MimeType is "image/jpeg" or "image/png")
            return new(document.FileId, document.FileUniqueId, document.FileName, document.MimeType, document.FileSize, null, null);
        return null;
    }
}
