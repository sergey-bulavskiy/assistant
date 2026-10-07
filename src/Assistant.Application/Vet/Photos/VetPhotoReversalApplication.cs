using System.Globalization;
using System.Text.Json;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public sealed class VetPhotoReversalApplication(IVetPhotoReversalStore reversals,
    IVetPhotoWorkflowStore workflow, VetPhotoReviewComposer composer, IApprovalService approvals)
    : IVetPhotoReversalApplication
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Task Say(ITelegramClient client, VetDiaryScope scope, int? reply, string text, CancellationToken ct) =>
        client.SendTextAsync(scope.ChatId, scope.TopicId, text, reply, ct);
    public async Task StageAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation op, Guid operationKey, CancellationToken ct)
    {
        if (bot.Role != "vet" || bot.FamilyId is not { } family || message.UserId is not { } actor
            || await approvals.FindFamilyMemberStatusAsync(family, actor, ct) != FamilyMemberStatus.Approved
            || !(message.ChatType == "private" && message.ChatId == actor && message.TopicId == null
                || await approvals.FindPlaceStatusAsync(bot.BotDbId, message.ChatId, message.TopicId, ct) == PlaceStatus.Approved)) return;
        var scope = VetDiaryScope.From(bot, message);
        if (op.Kind != "reverse" || operationKey == Guid.Empty || op.ActionId is not > 0
            || op.CandidateIds.Count > 50 || op.CandidateIds.Distinct().Count() != op.CandidateIds.Count
            || op.SourceIds.Count != 0 || op.ItemNumbers.Count != 0 || op.BatchId != null || op.RunId != null || op.ReviewId != null)
        { await Say(client, scope, message.MessageId, "Укажите одно точное действие: полный набор до 50 кандидатов или от 1 до 50 точных кандидатов этого действия. Изменений пока нет.", ct); return; }
        var view = await reversals.ReadReversalAsync(scope, op.ActionId.Value, op.CandidateIds, actor, ct);
        if (view == null)
        { await Say(client, scope, message.MessageId, "Точный набор недоступен в этом месте или полное действие содержит больше 50 кандидатов. Укажите точное действие и поднабор до 50 кандидатов; изменений нет.", ct); return; }
        var preview = Render(view);
        if (!preview.Success)
        { await Say(client, scope, message.MessageId, "Полный просмотр недоступен или не помещается. Выберите меньший точный набор; изменений нет.", ct); return; }
        var selection = JsonSerializer.Serialize(view.Items.Select(i => i.Entry).ToArray(), Json);
        var staged = await workflow.StageReviewAsync(new(scope, operationKey, actor, VetPhotoReviewKind.Reverse,
            null, null, view.ProfileRevision, selection, preview), ct);
        if (staged.Review == null)
        { await Say(client, scope, message.MessageId, "Просмотр устарел или конфликтует с прежним выбором. Изменений нет.", ct); return; }
        if (!staged.Review.CompletePreviewDelivered)
            await composer.DeliverAsync(scope, staged.Review, actor, client, message.MessageId, ct);
    }
    public async Task<bool> ConfirmAsync(VetDiaryScope scope, VetPhotoReview review, long actorUserId,
        int? callbackPromptMessageId, ITelegramClient client, int? replyToMessageId, CancellationToken ct)
    {
        if (review.Kind != "reverse") return false;
        var result = await reversals.ApplyReversalAsync(new(scope, review.Id, review.Revision,
            review.OperationKey, actorUserId, callbackPromptMessageId), ct);
        await Say(client, scope, replyToMessageId, $"Обратный выбранный набор: {result.Status}; изменено фактов {result.EventIds.Count}; защищено фактов {result.ProtectedIds.Count}, кандидатов {result.ProtectedCandidateIds.Count}. Подписи и оригиналы не меняются.", ct);
        return true;
    }
    public static VetPhotoPreviewResult Render(VetPhotoReversalPreview view)
    {
        var blocks = new List<string>();
        try
        {
        foreach (var item in view.Items)
        {
            var e = item.Entry;
            blocks.Add($"Кандидат {e.CandidateId:D}, источник {e.SourceId:D}, автор {item.SourceAuthorUserId}; вход {item.InputRevisionId}, результат {item.ExtractionResultId}; текущий вход {e.CurrentInputId:D}, версия {e.SourceOrdinal}. Свой факт {e.EventId}, версия {e.EventRevision}; связанный канонический факт {e.LinkEventId}, версия {e.LinkEventRevision}.");
            blocks.Add("Полное состояние после исходного действия (основание сравнения): " + Candidate(item.OriginalAfter) + "\n" + Event(item.OriginalEventAfter));
            blocks.Add("До исходного действия — отличия от показанного состояния после: кандидат: " + Differences(item.OriginalAfter, item.OriginalBefore)
                + "\nСобственный факт: " + EventDifferences(item.OriginalEventAfter, item.OriginalEventBefore));
            blocks.Add("Сейчас — отличия от показанного состояния после: кандидат: " + Differences(item.OriginalAfter, item.CurrentCandidate)
                + "\nСобственный факт: " + EventDifferences(item.OriginalEventAfter, item.CurrentEvent));
            if (item.Entry.LinkEventId != null) blocks.Add("Связанный канонический факт сейчас: " + Event(item.CurrentLinkedEvent));
            blocks.Add(e.CandidateProtected || e.EventProtected
                ? "ЗАЩИЩЕНО: этот кандидат/факт уже изменён. Полностью показанные текущие данные останутся без изменений."
                : "Обратная операция — отличия от показанного состояния после: кандидат: " + (item.InverseCandidate == null ? "без изменений относительно показанного текущего состояния" : Differences(item.OriginalAfter, item.InverseCandidate))
                    + "\nСобственный факт: " + (item.InverseEvent == null ? "без изменений относительно показанного текущего состояния" : EventDifferences(item.OriginalEventAfter, item.InverseEvent))
                    + "\nАвтор и время подтверждения удаления будут записаны при принятии. Связь без собственного факта не меняет канонический факт.");
        }
        }
        catch (JsonException) { return new([], "stored_reversal_data_invalid"); }
        return VetPhotoReviewFormatter.FormatBlocks($"Обратный набор для действия {view.ActionId}, исходный участник {view.OriginalActorUserId}, время действия {view.OriginalActionAt:O}. Выбрано {view.Items.Count}; профиль {view.ProfileId}, версия {view.ProfileRevision}.", blocks,
            "Принятие меняет только полностью показанный набор. Защищённые строки сохраняются. Подписи, отдельные записи инсулина, байты и прежние принятые просмотры сохраняются. Любое изменение показанного текущего состояния делает весь просмотр устаревшим.");
    }
    private static string Candidate(VetPhotoCandidateState state)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(state, Json));
        return Fields(document.RootElement.EnumerateObject());
    }
    private static string EventDifferences(VetEventState? shownAfter, VetEventState? value)
    {
        var summary = value != null && (shownAfter == null || value.Value != shownAfter.Value
            || value.Unit != shownAfter.Unit || value.OccurredAt != shownAfter.OccurredAt)
            ? $"Измерение: {value.Value.ToString(CultureInfo.InvariantCulture)} {value.Unit}; время {value.OccurredAt:O}; " : "";
        return summary + Differences(shownAfter, value);
    }
    private static string Differences(object? shownAfter, object? value)
    {
        if (value == null) return "состояния нет";
        using var after = JsonDocument.Parse(JsonSerializer.Serialize(shownAfter, Json));
        using var target = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
        var changes = target.RootElement.EnumerateObject().Where(p => after.RootElement.ValueKind != JsonValueKind.Object
            || !after.RootElement.TryGetProperty(p.Name, out var prior) || p.Value.GetRawText() != prior.GetRawText()).ToArray();
        return changes.Length == 0 ? "все поля точно как в полностью показанном состоянии после"
            : Fields(changes) + "; остальные поля точно как в полностью показанном состоянии после";
    }
    private static string Fields(IEnumerable<JsonProperty> fields) => string.Join("; ", fields.Select(p => Label(p.Name) + ": "
        + (p.Name.EndsWith("Json", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String
            ? Data(p.Value.GetString()!) : Data(p.Value.GetRawText()))));
    private static string Data(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        string Describe(JsonElement e) => e.ValueKind switch {
            JsonValueKind.Object => e.EnumerateObject().Any() ? string.Join("; ", e.EnumerateObject().Select(p => Label(p.Name) + ": " + Describe(p.Value))) : "нет",
            JsonValueKind.Array => e.GetArrayLength() == 0 ? "нет" : string.Join("; ", e.EnumerateArray().Select(Describe)),
            JsonValueKind.String => e.GetString() ?? "нет", JsonValueKind.Null => "нет",
            JsonValueKind.True => "да", JsonValueKind.False => "нет", _ => e.GetRawText() };
        return Describe(document.RootElement);
    }
    private static string Label(string name) => name switch {
        "value" => "значение", "rawValue" => "исходное значение", "unit" => "единица",
        "occurredAt" => "время", "localTime" => "местное время", "timeZoneSnapshot" => "часовой пояс",
        "valueEvidence" => "источник значения", "unitEvidence" => "источник единицы", "timeEvidence" => "основание времени",
        "usesProfileDefaults" => "использован профиль", "actorUserId" => "участник", "at" => "время поправки", "reading" => "измерение",
        "year" => "год", "month" => "месяц", "day" => "день", "time" => "время", "offset" => "смещение",
        "correctionApproved" => "поправка подтверждена", "yearBoundaryAmbiguous" => "год неоднозначен",
        "state" => "состояние", "requiresExplicitRestoration" => "явное восстановление", "manuallyCorrected" => "ручная поправка",
        "correctionProvenanceJson" => "пояснение поправки (данные)", "effectiveJson" => "эффективные данные (данные)", "reasonsJson" => "неопределённость (данные)",
        "duplicateDecision" => "решение о повторе", "duplicateSourceId" => "связанный источник", "duplicateEventId" => "связанный факт", "duplicateEventRevision" => "версия связанного факта",
        "eventId" => "свой факт", "eventRevision" => "версия своего факта", "inputRevisionId" => "вход", "extractionResultId" => "результат", "lastReviewId" => "предыдущий просмотр",
        "eventType" => "тип факта", "occurredAtSource" => "основание времени", "valueUnitSource" => "основание значения/единицы", "sourceKind" => "происхождение", "sourceId" => "источник",
        "candidateOrdinal" => "позиция", "textSourceId" => "источник TEXT", "photoSourceId" => "источник PHOTO", "photoBatchId" => "партия", "sourceAuthorUserId" => "автор",
        "sourceMessageDbId" => "сохранённое сообщение", "telegramMessageId" => "сообщение", "deletedAt" => "время удаления", "deleteReason" => "причина удаления", "deletedByUserId" => "участник удаления",
        "selectedDisplayIndex" => "выбранный экран", "preservedTime" => "сохранённое время", _ => name };
    private static string Event(VetEventState? state) => state == null ? "Собственного измерения нет."
        : $"Измерение {state.EventType}: {state.Value.ToString(CultureInfo.InvariantCulture)} {state.Unit}; продукт {state.Product}; время {state.OccurredAt:O}; локальное {state.LocalTime}; зона {state.TimeZoneSnapshot}; основание времени {state.OccurredAtSource}; основание значения/единицы {state.ValueUnitSource}; происхождение {state.SourceKind}, источник {state.SourceId}, автор {state.SourceAuthorUserId}; вход {state.InputRevisionId}; результат {state.ExtractionResultId}; удалено {state.DeletedAt != null}; время удаления {state.DeletedAt:O}; причина {state.DeleteReason}; участник удаления {state.DeletedByUserId}; позиция {state.CandidateOrdinal}; TEXT {state.TextSourceId}; PHOTO {state.PhotoSourceId}; партия {state.PhotoBatchId}; сохранённое сообщение {state.SourceMessageDbId}; сообщение {state.TelegramMessageId}.";
}
