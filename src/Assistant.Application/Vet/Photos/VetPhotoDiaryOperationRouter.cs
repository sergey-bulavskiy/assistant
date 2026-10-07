using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Messages;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;
using Assistant.Domain.Families;
using Assistant.Domain.Places;

namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoDiaryOperationRouter
{
    Task<bool> HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string kind, IReadOnlyList<long> targetIds, IReadOnlyList<VetCandidate> candidates,
        Guid operationKey, CancellationToken cancellationToken);
}

public sealed class VetPhotoDiaryOperationRouter(IVetDiaryStore diary,
    IVetPhotoArchiveStore archive, IVetPhotoWorkflowStore workflow, IVetPhotoPresentationStore presentation,
    IVetPhotoCommandReceiptStore receipts, IApprovalService approvals,
    VetPhotoDispositionComposer dispositions, VetPhotoReviewComposer reviews) : IVetPhotoDiaryOperationRouter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<bool> HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string kind, IReadOnlyList<long> targetIds, IReadOnlyList<VetCandidate> candidates,
        Guid operationKey, CancellationToken ct)
    {
        if (kind is not ("correct" or "delete") || bot.FamilyId == null || message.UserId is not { } actor) return false;
        if (!BotRoles.IsVet(bot.Role) || await approvals.FindFamilyMemberStatusAsync(bot.FamilyId.Value, actor, ct) != FamilyMemberStatus.Approved
            || !(message.ChatType == "private" && message.ChatId == actor && message.TopicId == null
                || message.ChatType is "group" or "supergroup"
                    && await approvals.FindPlaceStatusAsync(bot.BotDbId, message.ChatId, message.TopicId, ct) == PlaceStatus.Approved)) return true;
        var scope = VetDiaryScope.From(bot, message);
        var request = JsonSerializer.Serialize(new { kind, targetIds, candidates, message.ReplyToMessageId }, Json);
        if (kind == "correct")
        {
            var prior = await receipts.ReadPlanAsync(scope, operationKey, actor, ct);
            if (prior.Plan != null)
            {
                if (prior.Plan.RequestJson != request)
                    await Say(client, message, "Сохранённое исправление имеет другое содержание; ничего не изменено.", ct);
                else await Execute(prior.Plan, bot, client, message, ct);
                return true;
            }
        }
        var ids = targetIds.Concat(candidates.Where(c => c.EventId != null).Select(c => c.EventId!.Value)).Distinct().ToArray();
        if (ids.Length == 0 && message.ReplyToMessageId is { } reply)
        {
            var replied = await archive.FindSourceAsync(scope, reply, ct);
            if (replied?.Source?.BatchId is { } batchId)
            {
                var batch = await workflow.GetBatchAsync(scope, batchId, actor, ct);
                ids = batch?.Items.Where(i => i.Source.Id == replied.Source.Id && i.Candidate.EventId != null)
                    .Select(i => i.Candidate.EventId!.Value).ToArray() ?? [];
            }
        }
        if (ids.Length is < 1 or > 50) return false;
        var targets = new List<VetEvent>();
        foreach (var id in ids)
        {
            var found = await diary.GetEventAsync(scope, id, ct);
            if (found == null) return false;
            targets.Add(found);
        }
        if (targets.All(e => e.SourceKind != "photo")) return false;
        if (targets.Any(e => e.SourceKind != "photo" || e.PhotoSourceId == null || e.PhotoBatchId == null))
        { await Say(client, message, "Выберите фото-записи отдельно от текстовых записей, чтобы полный просмотр точно описывал выбранный набор.", ct); return true; }
        var batches = new Dictionary<Guid, VetPhotoBatchSnapshot>();
        foreach (var batchId in targets.Select(e => e.PhotoBatchId!.Value).Distinct())
        {
            var batch = await workflow.GetBatchAsync(scope, batchId, actor, ct);
            if (batch == null) return true;
            batches.Add(batchId, batch);
        }
        var selected = batches.Values.SelectMany(b => b.Items).Where(i => targets.Any(e => e.Id == i.Candidate.EventId
            && e.PhotoSourceId == i.Source.Id && e.Revision == i.Candidate.EventRevision)).ToArray();
        if (selected.Length != targets.Count)
        { await Say(client, message, "Версия фото-записи изменилась; нужен новый точный просмотр.", ct); return true; }
        if (kind == "delete")
        {
            // Each complete batch preview is a separate exact subset; no unseen batch is accepted with it.
            foreach (var group in selected.GroupBy(i => i.Candidate.BatchId!.Value))
            {
                var built = await dispositions.BuildAsync(scope, group.Key, actor,
                    group.Select(i => i.Candidate.Id).ToArray(), "delete", ct, Child(operationKey, "delete:" + group.Key.ToString("D")));
                await Deliver(built, scope, actor, client, message, ct);
            }
            return true;
        }
        // An independent actual insulin record is processed by the TEXT pipeline, not as a photo correction.
        var changes = candidates.Where(c => c.Intent != "question_only"
            && (c.EventId != null || c.EventType == "glucose")).ToArray();
        if (changes.Length == 0 || changes.Length != targets.Count
            || changes.Any(c => c.EventType != "glucose" || c.EventId == null && targets.Count != 1)
            || changes.Select(c => c.EventId ?? targets[0].Id).Distinct().Count() != changes.Length)
        { await Say(client, message, "Укажите одно точное изменение для каждой выбранной фото-записи; предложение не перезаписано.", ct); return true; }
        var steps = new List<VetPhotoCommandStep>();
        var increments = new Dictionary<Guid, int>();
        foreach (var change in changes)
        {
            var target = targets.SingleOrDefault(e => e.Id == (change.EventId ?? targets[0].Id));
            if (target == null || change.Unresolved != null
                || change.Date != null && !DateOnly.TryParseExact(change.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
            { await Say(client, message, "Нужны точное значение и допустимая дата выбранной записи.", ct); return true; }
            var item = selected.Single(i => i.Candidate.EventId == target.Id);
            var batch = batches[target.PhotoBatchId!.Value];
            var evidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, item.Input.Id, null, actor, ct);
            if (evidence?.OwnedEvent?.Id != target.Id || evidence.OwnedEvent.Revision != target.Revision)
            { await Say(client, message, "Исходные ссылки фото-записи изменились; нужен новый просмотр.", ct); return true; }
            var date = change.Date == null ? (DateOnly?)null : DateOnly.ParseExact(change.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var context = new VetPhotoContext(change.RawValue, change.Unit ?? target.Unit, date?.Year, date?.Month,
                date?.Day, change.Time, change.Offset, CorrectionApproved: true);
            var priorCount = increments.GetValueOrDefault(batch.Batch.Id);
            steps.Add(new("human", Human: new(scope, batch.Batch.Id, checked(batch.Batch.ReviewRevision + priorCount),
                item.Candidate.Id, item.Candidate.Revision, item.Input.Id, item.Source.CurrentOrdinal, actor, context)));
            increments[batch.Batch.Id] = priorCount + 1;
        }
        var prepared = await receipts.PreparePlanAsync(new(scope, operationKey, actor, request, steps), ct);
        if (prepared.Plan == null)
        { await Say(client, message, "Исправление не подготовлено: вход или выбранная версия изменилась.", ct); return true; }
        await Execute(prepared.Plan, bot, client, message, ct);
        return true;
    }
    private async Task Execute(VetPhotoCommandPlan plan, ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, CancellationToken ct)
    {
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var outcome = await receipts.ExecuteStepAsync(plan.Scope, plan.SourceOperationKey, index, plan.ActorUserId, ct);
            if (outcome.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
            { await Say(client, message, "Исправление удержано: выбранная версия изменилась. Уже подготовленные части сохранены без записи новых фактов.", ct); return; }
        }
        foreach (var group in plan.Steps.GroupBy(s => s.Human!.BatchId))
        {
            var built = await reviews.BuildBatchAsync(plan.Scope, group.Key, plan.ActorUserId,
                group.Select(s => s.Human!.CandidateId).ToArray(), false, ct,
                Child(plan.SourceOperationKey, "correct:" + group.Key.ToString("D")), durableEvidence: true);
            await Deliver(built, plan.Scope, plan.ActorUserId, client, message, ct);
        }
    }
    private async Task Deliver(VetPhotoComposedReview built, VetDiaryScope scope, long actor,
        ITelegramClient client, IncomingMessage message, CancellationToken ct)
    {
        if (built.Review != null) await reviews.DeliverAsync(scope, built.Review, actor, client, message.MessageId, ct);
        else if (built.Pages.Count > 0)
            foreach (var page in built.Pages) await Say(client, message, page, ct);
        else await Say(client, message, built.StatusText, ct);
    }
    private static Guid Child(Guid key, string purpose) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes("photo-diary:" + key.ToString("D") + ":" + purpose)).AsSpan(0, 16));
    private static Task<int> Say(ITelegramClient client, IncomingMessage message, string text, CancellationToken ct) =>
        client.SendTextAsync(message.ChatId, message.TopicId, text, message.MessageId, ct);
}
