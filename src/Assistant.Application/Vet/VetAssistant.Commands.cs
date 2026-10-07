using System.Globalization;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;

namespace Assistant.Application.Vet;

public sealed partial class VetAssistant
{
    private const string StartText = "Дневник глюкозы и фактически введённого инсулина для кота этого бота. "
        + "Можно писать обычным текстом, задавать вопросы, получать историю и исправлять записи. "
        + "Владелец один раз настраивает часовой пояс и единицы. "
        + "/profile /setname /settz /setunit /setinsulin /setnote [owner|vet] /today /more /undo /del /retry /version. "
        + "В одобренном месте запись работает без упоминания; ответы без упоминания включаются владельцем в /settings.";

    private async Task CommandAsync(string command, string? args, ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetAdmittedSource source, VetProfile profile, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        switch (command)
        {
            case "start":
                if (message.ChatType == "private") await _replies.SendAsync(client, message, StartText, ct);
                return;
            case "profile": await _replies.SendAsync(client, message, VetEventText.Profile(profile), ct); return;
            case "version":
                await _replies.SendAsync(client, message, VersionText.Format(_buildInfo, _clock.UtcNow), ct); return;
            case "setname":
            case "settz":
            case "setunit":
            case "setinsulin":
            case "setnote":
            {
                var changes = ProfileCommand(command, args);
                if (changes is null)
                    await _replies.SendAsync(client, message, "Укажите значение. /setunit mmol/L; /setinsulin <препарат> U; /setnote [owner|vet] <текст>. '-' очищает поле.", ct);
                else await ProfileAsync(bot, client, message, profile, changes, ct);
                return;
            }
            case "today":
            {
                if (profile.TimeZone is null)
                { await _replies.SendAsync(client, message, "Сначала настройте часовой пояс: /settz <IANA>.", ct); return; }
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(source.Source.SentAt,
                    TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                await HistoryAsync(bot, client, message, source, profile, new(today, today, 0, false), ct);
                return;
            }
            case "more": await ContinueAsync(bot, client, message, source, profile, ct); return;
            case "undo":
            {
                var result = await _diary.UndoAsync(scope, message.UserId!.Value, source.Revision.OperationKey, ct);
                await _replies.SendAsync(client, message, UndoText(result), ct);
                return;
            }
            case "del":
            {
                long? id = null;
                if (args is not null)
                {
                    if (!long.TryParse(args.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                    { await _replies.SendAsync(client, message, "Используйте /del <ID> или ответ на исходное сообщение.", ct); return; }
                    id = parsed;
                }
                var targets = await TargetsAsync(scope, message, id, [], ct);
                await DeleteAsync(bot, client, message, source, profile, targets, false, ct);
                return;
            }
            case "retry": await RetrySourceAsync(bot, client, message, source, ct); return;
            default:
                await _replies.SendAsync(client, message, StartText, ct); return;
        }
    }

    private static IReadOnlyList<VetProfileChange>? ProfileCommand(string command, string? args)
    {
        if (args is null) return null;
        string? Value(string value) => value == "-" ? null : value;
        if (command == "setnote")
        {
            var field = "OwnerContextNote";
            if (args.StartsWith("owner ", StringComparison.Ordinal)) args = args[6..];
            else if (args.StartsWith("vet ", StringComparison.Ordinal)) { field = "ReportedVetGuidance"; args = args[4..]; }
            return [new(field, Value(args))];
        }
        if (command == "setinsulin")
        {
            if (args == "U") return [new("InsulinUnit", "U")];
            if (args == "-") return [new("InsulinProduct", null)];
            if (args.EndsWith(" U", StringComparison.Ordinal))
                return [new("InsulinProduct", Value(args[..^2].Trim())), new("InsulinUnit", "U")];
            return [new("InsulinProduct", args)];
        }
        return [new(command switch { "setname" => "Name", "settz" => "TimeZone", _ => "GlucoseUnit" }, Value(args))];
    }

    private async Task ProfileAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetProfile profile, IReadOnlyList<VetProfileChange> changes, CancellationToken ct)
    {
        if (!await _ownership.IsApprovedOwnerAsync(bot.FamilyId!.Value, message.UserId!.Value, ct))
        { await _replies.SendAsync(client, message, OwnerOnlyText, ct); return; }
        if (changes.Count == 0 || changes.Any(c => !VetProfileValidation.IsValid(c.Field, c.Value)))
        { await _replies.SendAsync(client, message, "Уточните поле профиля: IANA часовой пояс, mmol/L, U и допустимая длина текста.", ct); return; }
        var result = await _profiles.UpdateAsync(bot.FamilyId.Value, bot.BotDbId, message.UserId.Value, profile.Revision, changes, ct);
        await _replies.SendAsync(client, message, result.Stale
            ? "Профиль уже изменился: проверьте /profile и повторите нужное изменение."
            : result.Applied ? "Профиль обновлён. Изменение действует для будущих записей.\n" + VetEventText.Profile(result.Profile) : OwnerOnlyText, ct);
    }

    private async Task<bool> OperationAsync(VetInterpretation interpretation, ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetAdmittedSource source, VetProfile profile, CancellationToken ct)
    {
        if (interpretation.Operation is not { } op) return false;
        var scope = VetDiaryScope.From(bot, message);
        switch (op.Kind)
        {
            case "profile":
                await ProfileAsync(bot, client, message, profile, op.ProfileChanges, ct); return true;
            case "undo":
                await _replies.SendAsync(client, message,
                    UndoText(await _diary.UndoAsync(scope, message.UserId!.Value, source.Revision.OperationKey, ct)), ct); return true;
            case "delete":
                await DeleteAsync(bot, client, message, source, profile,
                    await TargetsAsync(scope, message, op.EventId, op.EventIds, ct), true, ct); return true;
            case "correct":
            {
                var result = await _diary.GetResultAsync(scope, source.Revision.Id, ct);
                var targets = await TargetsAsync(scope, message, op.EventId, op.EventIds, ct);
                var changes = new List<VetEventChange>();
                var reasons = new List<string>();
                var candidates = interpretation.Events.Where(c => c.Intent != "question_only").ToList();
                foreach (var c in candidates)
                {
                    var target = c.EventId is { } id ? await _diary.GetEventAsync(scope, id, ct)
                        : targets.Count == 1 && candidates.Count == 1 ? targets[0] : null;
                    if (target is null) { reasons.Add("Укажите ID каждой исправляемой записи."); continue; }
                    var validation = VetEventValidation.Validate(c with { Intent = "record" }, profile, source, result!.Id);
                    if (validation.State is null) { reasons.Add(validation.Reason ?? "Уточните исправление."); continue; }
                    if (validation.State.EventType != target.EventType) { reasons.Add("Тип существующего факта не меняется."); continue; }
                    var original = VetEventText.State(target);
                    changes.Add(new(target.Id, target.Revision, validation.State with
                    {
                        SourceKind = original.SourceKind, SourceId = original.SourceId, CandidateOrdinal = original.CandidateOrdinal,
                        TextSourceId = original.TextSourceId, SourceAuthorUserId = original.SourceAuthorUserId,
                        SourceMessageDbId = original.SourceMessageDbId, TelegramMessageId = original.TelegramMessageId
                    }));
                }
                if (changes.Count == 0 && reasons.Count == 0) reasons.Add("Укажите запись и её новое точное значение/время.");
                var proposal = new VetProposal(candidates, changes, reasons, "manual");
                if (changes.Count == 1 && reasons.Count == 0)
                {
                    var mutation = new VetDiaryMutation(scope, source.Revision.OperationKey, message.UserId!.Value,
                        "manual", profile.Id, changes, source.Source.Id, source.Revision.Id);
                    var outcome = await ApplyPlannedAsync(source, mutation, ct);
                    await _replies.SendAsync(client, message, MutationText(changes, outcome), ct);
                }
                else
                {
                    var decision = await _diary.PutPendingAsync(scope, source.Source.Id, source.Revision.Id, result!.Id,
                        message.UserId!.Value, JsonSerializer.Serialize(proposal), ct);
                    await ShowPendingAsync(client, message, decision, profile, ct);
                }
                return true;
            }
            case "accept":
            case "decline":
                await ResolveNaturalAsync(op, interpretation.Events, bot, client, message, profile, ct); return true;
            case "continue": await ContinueAsync(bot, client, message, source, profile, ct); return true;
            case "retry": await RetrySourceAsync(bot, client, message, source, ct); return true;
            default: return false;
        }
    }

    private async Task<IReadOnlyList<VetEvent>> TargetsAsync(VetDiaryScope scope, IncomingMessage message,
        long? id, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var selected = ids.Concat(id is { } n ? new[] { n } : []).Distinct().ToArray();
        if (selected.Length > 0)
        {
            var rows = new List<VetEvent>();
            foreach (var eventId in selected)
            {
                var row = await _diary.GetEventAsync(scope, eventId, ct);
                if (row is null) return [];
                rows.Add(row);
            }
            return rows;
        }
        if (message.ReplyToMessageId is not { } reply) return [];
        var target = await _diary.FindSourceAsync(scope, reply, ct);
        return target is null ? [] : await _diary.GetSourceEventsAsync(scope, target.Source.Id, ct);
    }

    private async Task DeleteAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource source, VetProfile profile, IReadOnlyList<VetEvent> targets, bool natural, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        var changes = targets.Where(e => e.DeletedAt is null).Select(e => new VetEventChange(e.Id, e.Revision,
            VetEventText.State(e) with { DeletedAt = _clock.UtcNow, DeleteReason = "manual", DeletedByUserId = message.UserId })).ToArray();
        if (changes.Length == 0) { await _replies.SendAsync(client, message, "Активная запись в этом месте не найдена. Укажите ID или ответьте на её источник.", ct); return; }
        if (natural && changes.Length > 1)
        {
            var extracted = await _diary.GetResultAsync(scope, source.Revision.Id, ct);
            var decision = await _diary.PutPendingAsync(scope, source.Source.Id, source.Revision.Id, extracted!.Id,
                message.UserId!.Value, JsonSerializer.Serialize(new VetProposal([], changes, [], "delete")), ct);
            await ShowPendingAsync(client, message, decision, profile, ct); return;
        }
        var mutation = new VetDiaryMutation(scope, source.Revision.OperationKey, message.UserId!.Value,
            "delete", profile.Id, changes);
        var outcome = await ApplyPlannedAsync(source, mutation, ct);
        await _replies.SendAsync(client, message, MutationText(changes, outcome), ct);
    }

    private async Task<VetMutationResult> ApplyPlannedAsync(VetAdmittedSource source, VetDiaryMutation proposed, CancellationToken ct)
    {
        var current = await _diary.GetSourceAsync(proposed.Scope, source.Source.Id, ct);
        VetDiaryMutation mutation;
        if (current!.Revision.WorkJson is { } json) mutation = JsonSerializer.Deserialize<VetDiaryMutation>(json)!;
        else
        {
            await _diary.SaveWorkAsync(proposed.Scope, source.Revision.Id, JsonSerializer.Serialize(proposed), ct);
            mutation = proposed;
        }
        return await _diary.ApplyAsync(mutation, ct);
    }

    private async Task RetrySourceAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource commandSource, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        var target = message.ReplyToMessageId is { } reply ? await _diary.FindSourceAsync(scope, reply, ct) : null;
        if (target is null || target.Source.Id == commandSource.Source.Id)
        { await _replies.SendAsync(client, message, "Используйте /retry ответом на исходное сообщение.", ct); return; }
        if (!await _diary.RetryAsync(scope, target.Source.Id, ct))
        { await _replies.SendAsync(client, message, "Повтор сейчас недоступен: обработка уже завершена или достигнут предел трёх явных попыток.", ct); return; }
        var refreshed = (await _diary.GetSourceAsync(scope, target.Source.Id, ct))!;
        await _replies.SendAsync(client, message, "Повтор разрешён. Сохранённый результат будет использован без нового вызова модели, если он уже есть.", ct);
        // The regular bounded recovery loop processes this exact admitted input after fresh auth.
    }

    private static string MutationText(IReadOnlyList<VetEventChange> changes, VetMutationResult result) =>
        result.Status is VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied or VetMutationStatus.NoChange
            ? DescribeChanges(changes, result) : "Ничего не изменено: запись/просмотр уже обновились или недоступны в этом месте.";
    private static string UndoText(VetMutationResult result) => result.Status is VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied
        ? $"Отменено записей: {result.EventIds.Count}. Защищено более поздних изменений: {result.ProtectedIds.Count}."
            + (result.ProtectedIds.Count > 0 ? " ID: " + string.Join(", ", result.ProtectedIds.Select(id => $"#{id}")) : "")
        : "Нет вашей неотменённой операции за последние 24 часа в этом месте.";
}
