using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Domain.Vet;

namespace Assistant.Application.Vet.Photos;

public sealed class VetPhotoDispositionComposer(IVetPhotoWorkflowStore workflow,
    IVetPhotoPresentationStore presentation, IVetProfileStore profiles,
    IVetPhotoDiaryStore diary, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VetPhotoComposedReview> BuildAsync(VetDiaryScope scope, Guid batchId,
        long actorUserId, IReadOnlyList<Guid> selectedCandidateIds, string disposition, CancellationToken ct,
        Guid? operationKey = null)
    {
        if (disposition is not ("exclude" or "cancel" or "delete") || selectedCandidateIds == null
            || selectedCandidateIds.Count is < 1 or > 50
            || selectedCandidateIds.Distinct().Count() != selectedCandidateIds.Count)
            return Refuse("Нужен точный набор до 50 источников без повторов.");
        var batch = await workflow.GetBatchAsync(scope, batchId, actorUserId, ct);
        if (batch == null) return Refuse("Партия не найдена в этом месте.");
        if (batch.Items.Count > 50 || selectedCandidateIds.Any(id => !batch.Items.Any(i => i.Candidate.Id == id)))
            return Refuse("Выбранный набор не относится к этой партии.");
        if (batch.Batch.State == "collecting")
            return Refuse("Сначала явно завершите приём: /photos_close.");
        var profile = await profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, ct);
        var refreshed = await presentation.RefreshProfileSnapshotAsync(scope, batchId, batch.Batch.ReviewRevision,
            profile.Revision, actorUserId, ct);
        if (refreshed.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
            return Refuse("Профиль или партия изменились; нужен свежий просмотр.");
        batch = await workflow.GetBatchAsync(scope, batchId, actorUserId, ct);
        if (batch == null) return Refuse("Партия больше не доступна.");
        var selected = new List<VetPhotoDiarySelection>();
        var blocks = new List<string>();
        var protectedCount = 0;
        foreach (var item in batch.Items.OrderBy(i => i.Source.ItemNumber))
        {
            var chosen = selectedCandidateIds.Contains(item.Candidate.Id);
            var evidence = await presentation.ReadEvidenceAsync(scope, item.Source.Id, item.Input.Id,
                null, actorUserId, ct);
            if (evidence == null || evidence.Candidate.Revision != item.Candidate.Revision
                || evidence.Source.CurrentInputRevisionId != item.Input.Id)
                return Refuse("Источник изменился во время подготовки; ничего не применено.");
            var liveFact = evidence.OwnedEvent is { DeletedAt: null };
            var savedLink = evidence.Candidate.State == "linked";
            var change = chosen && (disposition == "delete" ? liveFact : !liveFact && !savedLink
                && evidence.Candidate.State is not ("excluded" or "cancelled" or "deleted"));
            if (chosen && !change) protectedCount++;
            var header = "#" + item.Source.ItemNumber?.ToString(CultureInfo.InvariantCulture)
                + "; источник " + item.Source.Id.ToString("D") + "; кандидат " + item.Candidate.Id.ToString("D")
                + " rev " + item.Candidate.Revision.ToString(CultureInfo.InvariantCulture)
                + "\nТекущий вход " + item.Input.Id.ToString("D") + "; состояние " + item.Candidate.State
                + "\n" + (change ? disposition switch { "delete" => "Удалить только показанный факт дневника.",
                    "exclude" => "Исключить этот несохранённый кандидат.", _ => "Отменить этот несохранённый кандидат." }
                    : "Оставить без изменений.");
            if (evidence.OwnedEvent is { } existing)
                header += "\nФакт #" + existing.Id.ToString(CultureInfo.InvariantCulture) + " rev "
                    + existing.Revision.ToString(CultureInfo.InvariantCulture) + ": "
                    + existing.Value.ToString(CultureInfo.InvariantCulture) + " " + existing.Unit
                    + "; " + existing.LocalTime + "; зона " + existing.TimeZoneSnapshot
                    + "; UTC " + existing.OccurredAt.ToUniversalTime().ToString("O");
            if (evidence.Candidate.DuplicateEventId is { } link)
                header += "\nСвязь с фактом #" + link.ToString(CultureInfo.InvariantCulture)
                    + " rev " + evidence.Candidate.DuplicateEventRevision?.ToString(CultureInfo.InvariantCulture);
            if (change && disposition == "delete")
            {
                // The fact retains its original immutable input/result even after a later caption edit.
                var owned = evidence.OwnedEvent!;
                var old = await presentation.ReadEvidenceAsync(scope, item.Source.Id, owned.InputRevisionId,
                    owned.ExtractionResultId, actorUserId, ct);
                if (old?.Extraction == null || old.OwnedEvent?.Id != owned.Id || old.OwnedEvent.Revision != owned.Revision)
                    return Refuse("Исходные ссылки факта изменились; нужен новый просмотр.");
                evidence = old;
                header += "\nФакт создан из входа " + owned.InputRevisionId.ToString("D")
                    + ", результата " + owned.ExtractionResultId.ToString("D") + ".";
            }
            if (evidence.Original is { } original)
                header += "\nОригинал " + original.Id.ToString("D") + " rev "
                    + original.Revision.ToString(CultureInfo.InvariantCulture) + ": " + original.State + ".";
            header += "\nБайты оригиналов, другие факты и независимая запись инсулина не меняются.";
            blocks.Add(header);
            if (!change) continue;
            VetEventState? state = disposition == "delete" ? EventState(evidence.OwnedEvent!) with
                { DeletedAt = clock.UtcNow, DeleteReason = "photo_delete", DeletedByUserId = null } : null;
            var collision = await diary.GetPhotoCollisionProofAsync(scope, profile.Id, item.Candidate.Id, state, ct);
            if (collision == null) return Refuse("Набор изменился; нужен новый просмотр.");
            selected.Add(new(profile.Id, profile.Revision, batchId, batch.Batch.ReviewRevision,
                item.Candidate.Id, item.Candidate.Revision, item.Source.Id, item.Source.CurrentInputRevisionId,
                item.Source.CurrentOrdinal, evidence.Input.Id, evidence.Extraction?.Id, item.Candidate.ExtractionResultId,
                evidence.Original?.Id, evidence.Original?.Revision, evidence.Original?.State,
                item.Candidate.EventId, item.Candidate.EventRevision, disposition, item.Candidate.DuplicateDecision,
                null, null, null, false, new(), state, collision));
        }
        var preview = VetPhotoReviewFormatter.FormatBlocks("Партия " + batchId.ToString("D")
            + "; выбранных источников " + selectedCandidateIds.Count.ToString(CultureInfo.InvariantCulture), blocks,
            "Подтверждение применит ровно " + selected.Count.ToString(CultureInfo.InvariantCulture)
            + " показанных изменений; защищённых/уже завершённых выбранных источников "
            + protectedCount.ToString(CultureInfo.InvariantCulture) + ". Прочие источники остаются прежними.");
        if (!preview.Success) return Refuse("Полный просмотр не помещается; выберите меньший точный набор.");
        if (selected.Count == 0)
            return new(VetPhotoWorkflowStatus.Incomplete, null, batch, "Применимых изменений нет.") { Pages = preview.Pages };
        var encoded = JsonSerializer.Serialize(selected, Json);
        var key = operationKey ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            "photo-disposition:" + disposition + ":" + encoded + ":" + string.Join("\n", preview.Pages))).AsSpan(0, 16));
        var staged = await workflow.StageReviewAsync(new(scope, key, actorUserId, VetPhotoReviewKind.Correction,
            batchId, batch.Batch.ReviewRevision, profile.Revision, encoded, preview), ct);
        return new(staged.Status, staged.Review, batch, "Полный набор показан для подтверждения.") { Pages = preview.Pages };
    }

    private static VetPhotoComposedReview Refuse(string text) => new(VetPhotoWorkflowStatus.Refused, null, null, text);
    private static VetEventState EventState(VetEvent e) => new(e.EventType, e.Value, e.Unit, e.Product,
        e.OccurredAt, e.LocalTime, e.TimeZoneSnapshot, e.OccurredAtSource, e.ValueUnitSource, e.SourceKind,
        e.SourceId, e.CandidateOrdinal, e.TextSourceId, e.PhotoSourceId, e.PhotoBatchId,
        e.InputRevisionId, e.ExtractionResultId, e.SourceAuthorUserId, e.SourceMessageDbId,
        e.TelegramMessageId, e.DeletedAt, e.DeleteReason, e.DeletedByUserId);
}
