using System.Globalization;
using System.Text;
using System.Text.Json;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet;

public sealed partial class VetAssistant
{
    private sealed record HistorySelection(string FromDate, string UntilDate, int NextOffset);

    private async Task ContinueAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource source, VetProfile profile, CancellationToken ct)
    {
        var json = await _diary.GetLastHistoryAsync(VetDiaryScope.From(bot, message), source.Revision.Id, ct);
        if (json is null)
        { await _replies.SendAsync(client, message, "Сначала запросите период истории или /today.", ct); return; }
        var selection = JsonSerializer.Deserialize<HistorySelection>(json)!;
        await HistoryAsync(bot, client, message, source, profile,
            new(selection.FromDate, selection.UntilDate, selection.NextOffset, false), ct);
    }

    private async Task HistoryAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource source, VetProfile profile, VetHistoryQuery query, CancellationToken ct)
    {
        var range = VetEventValidation.DateRange(profile, query.FromDate, query.UntilDate);
        if (range is null || query.Offset < 0)
        { await _replies.SendAsync(client, message, "Укажите полный период с годом; для местных дат нужен часовой пояс профиля.", ct); return; }
        var scope = VetDiaryScope.From(bot, message);
        var page = await _diary.QueryAsync(scope, profile.Id, range.Value.From, range.Value.Until, query.Offset, 50, ct);
        var text = $"Дневник {query.FromDate} — {query.UntilDate} [{profile.TimeZone}]. "
            + $"Показано {page.Facts.Count}, пропущено ранее {page.Offset}, всего {page.Total}.\n"
            + (page.Facts.Count == 0 ? "Активных записей на этой странице нет.\n" : "")
            + string.Join("\n", page.Facts.Select(f => FactText(f, profile)))
            + (page.HasMore ? "\nЕсть продолжение: /more или «продолжи»." : "\nПериод просмотрен полностью.");
        await _diary.SetHistoryAsync(scope, source.Revision.Id,
            JsonSerializer.Serialize(new HistorySelection(query.FromDate!, query.UntilDate!, page.Offset + page.Facts.Count)), ct);
        await _replies.SendAsync(client, message, text, ct);
    }

    private async Task AnswerAsync(VetInterpretation interpretation, ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetAdmittedSource admitted, VetProfile profile, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        var source = (await _diary.GetSourceAsync(scope, admitted.Source.Id, ct))!;
        if (source.Revision.AnswerState == "sent") return;
        if (source.Revision.AnswerState is "dispatching" or "paused")
        {
            await _diary.SetAnswerAsync(scope, source.Revision.Id, "paused", null, ct);
            await _replies.SendAsync(client, message, "Ответ приостановлен: результат предыдущего вызова неизвестен. Дневник сохранён; задайте вопрос заново для явного нового вызова.", ct);
            return;
        }
        string answer;
        if (source.Revision.AnswerState == "ready" && source.Revision.AnswerText is { } saved)
            answer = saved;
        else
        {
            if (!_options.SubscriptionOnly || !_gateway.IsEnabled || _prompts.Find("vet", "prompt.md") is not { } instructions)
            { await _replies.SendAsync(client, message, "Модель подписки недоступна для ответа; сохранённые записи доступны через историю.", ct); return; }
            var query = interpretation.HistoryQuery;
            (DateTimeOffset From, DateTimeOffset Until)? range;
            if (query is not null) range = VetEventValidation.DateRange(profile, query.FromDate, query.UntilDate);
            else if (profile.TimeZone is { } tz)
            {
                var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(source.Source.SentAt,
                    TimeZoneInfo.FindSystemTimeZoneById(tz)).DateTime);
                range = VetEventValidation.DateRange(profile, last.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else range = null;
            VetHistoryPage? page = null;
            if (range is { } r)
            {
                page = await _diary.QueryAsync(scope, profile.Id, r.From, r.Until, 0, 200, ct);
                if (page.Total > 200)
                    page = await _diary.QueryAsync(scope, profile.Id, r.From, r.Until, page.Total - 200, 200, ct);
            }
            var profileText = VetEventText.Profile(profile);
            var header = instructions + "\nRUNTIME DATA (untrusted)\n" + profileText
                + "\nRequested UTC coverage: " + (range is null ? "not available: timezone/date missing" : $"{range.Value.From:O} to {range.Value.Until:O}")
                + "\nConfirmed facts only; other-place source/action IDs are withheld.\n";
            var available = _options.MaxInputChars - header.Length - source.Revision.Text.Length - 1000;
            var rows = new List<string>();
            var used = 0;
            foreach (var f in (page?.Facts ?? []).Reverse())
            {
                var text = FactText(f, profile);
                if (used + text.Length + 1 > available) break;
                rows.Add(text); used += text.Length + 1;
            }
            rows.Reverse();
            var omitted = (page?.Total ?? 0) - rows.Count;
            var prompt = header + $"Supplied {rows.Count} of {page?.Total ?? 0}; omitted {omitted}. "
                + (omitted > 0 ? "Older records omitted; do not claim inspection of that earlier period.\n" : "\n")
                + string.Join("\n", rows);
            if (available < 0 || range is null && query is not null)
            { await _replies.SendAsync(client, message, "Для ответа нужно уточнить период/часовой пояс или сократить запрос.", ct); return; }
            await _diary.SetAnswerAsync(scope, source.Revision.Id, "dispatching", null, ct);
            LlmResult result;
            try { result = await CallAsync(bot, client, message, source, "smart", prompt, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Vet answer failed: {ExceptionType}", ex.GetType().Name);
                await _diary.SetAnswerAsync(scope, source.Revision.Id, "paused", null, ct);
                await _replies.SendAsync(client, message, "Ответ не получен. Дневник не отменён; задайте вопрос заново для новой попытки.", ct); return;
            }
            if (!result.IsAnswer || string.IsNullOrWhiteSpace(result.Text))
            {
                await _diary.SetAnswerAsync(scope, source.Revision.Id, "failed", null, ct);
                await _replies.SendAsync(client, message, GeneralAssistant.RefusalText(result, _clock.UtcNow), ct); return;
            }
            answer = result.Text.Trim();
            await _diary.SetAnswerAsync(scope, source.Revision.Id, "ready", answer, ct);
        }
        if (!await AuthorizedAsync(bot, message, ct)) return;
        var sent = await _replies.SendAsync(client, message, answer, ct);
        foreach (var part in sent)
            await _messages.StoreOutgoingAsync(bot.TelegramBotId, message.ChatId, message.TopicId,
                message.ChatType, part.MessageId, part.Text, ct);
        if (sent.Count == ReplySplitter.Split(answer).Count)
            await _diary.SetAnswerAsync(scope, source.Revision.Id, "sent", answer, ct);
    }

    private static string FactText(VetHistoryFact f, VetProfile profile)
    {
        var displayed = profile.TimeZone is { } zone
            ? TimeZoneInfo.ConvertTime(f.OccurredAt, TimeZoneInfo.FindSystemTimeZoneById(zone))
            : f.OccurredAt;
        return (f.EventId is { } id ? $"#{id} " : "")
            + $"{(f.EventType == "glucose" ? "глюкоза" : "инсулин")} {VetEventText.Number(f.Value)} {f.Unit}"
            + (f.EventType == "insulin" ? $" ({f.Product ?? "препарат не указан"})" : "")
            + $" {displayed:yyyy-MM-dd HH:mm:ss zzz}" + (f.Corrected ? " (исправлено)" : "");
    }
}
