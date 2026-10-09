using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Expectations;

public sealed class ExpectationAssistant(IExpectationStore store, ILogger<ExpectationAssistant> logger) : IExpectationAssistant
{
    public async Task<ExpectationIntake?> AdmitAsync(ReceivingBot bot, IncomingMessage message, CancellationToken ct)
    {
        if (bot.FamilyId is not { } family || message.Kind != MessageKind.Text || message.Text == null) return null;
        var command = ExpectationParser.Parse(message.Text, bot.Username, bot.Role);
        if (command == null) return null;
        var scope = new ReminderScope(family, bot.BotDbId, bot.TelegramBotId, bot.Role,
            message.ChatId, message.TopicId, message.ChatType, message.UserId ?? 0);
        if (message.UserId is not > 0 || message.ChatType == "private"
            && (message.ChatId != message.UserId || message.TopicId != null))
            return new(scope, "invalid", Result: "unavailable");
        if (message.IsEdit) return new(scope, "invalid", Result: "source_edit");
        if (command.Kind == "invalid") return new(scope, "invalid", Result: "help");
        try { return await store.ExecuteAsync(scope, message.MessageId, command, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ReminderPersistenceException.IsRetryable(ex))
        { throw new ReminderPersistenceException(); }
    }

    private static string Result(string? result) => result switch
    {
        "saved" => "Проверка сохранена. Начало или изменение — завтра в сохранённом смещении.",
        "pause" => "Проверка приостановлена. Возобновление требует нового подтверждения и начинается завтра.",
        "cancel" or "cancelled" => "Проверка или предложение отменены.",
        "pause_started" => "Проверка приостановлена; начатая отправка могла уже уйти и не может быть отозвана.",
        "cancel_started" => "Будущие проверки отменены; начатая отправка могла уже уйти и не может быть отозвана.",
        "duplicate" => "Проверка этого типа для этого профиля и места уже настроена.",
        "capacity" => "Достигнут общий предел напоминаний и проверок: 20 на автора и место, 200 на бота.",
        "profile_required" => "Сначала настройте профиль этого бота. Команда проверки не создаёт профиль.",
        "expired" => "Предложение устарело. Отправьте новую команду для нового предварительного просмотра.",
        "source_edit" => "Изменение сообщения не меняет проверки. Используйте /expect_edit или /expect_cancel.",
        "help" => ExpectationParser.Help,
        _ => "Недоступно или уже решено."
    };

    public async Task HandleAsync(ExpectationIntake intake, ITelegramClient client, IncomingMessage message,
        StoreResult stored, CancellationToken ct)
    {
        if (intake.Kind == "preview" && intake.DraftId is { } draft)
        {
            var p = await store.BeginPreviewAsync(intake.Scope, draft, ct); if (p == null) return;
            var text = "Предварительный просмотр. До Сохранить ничего не меняется.\n"
                + $"Профиль: {p.Subject}. Тип: {p.EventType}. Место: текущий чат и эта тема.\n"
                + $"Ежедневно до {ExpectationTimePolicy.Minute(p.DeadlineMinute)}, допуск {p.GraceMinutes} мин; "
                + $"UTC{ExpectationTimePolicy.Offset(p.OffsetMinutes)}, без летнего времени. С {p.EffectiveFrom:yyyy-MM-dd}.\n"
                + $"Учитываются подтверждённые записи с 00:00 до {ExpectationTimePolicy.Minute(p.DeadlineMinute + p.GraceMinutes)} включительно.\n"
                + ReminderAssistant.SettingsText(p.Preferences)
                + "\nПроверяется только отсутствие записи, а не выполнение действия. После подтверждения найденной записи день не открывается снова при её исправлении или удалении. "
                + "Отправка с неизвестным результатом автоматически не повторяется. Настройки смещения не сдвигают эту проверку.";
            try
            {
                var id = await client.SendTextWithButtonsAsync(message.ChatId, message.TopicId, text,
                    [new("Сохранить", $"exp_save:{p.DraftId:N}"), new("Отмена", $"exp_cancel:{p.DraftId:N}")], message.MessageId, ct);
                await store.BindPreviewAsync(intake.Scope, p.DraftId, id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning("Expectation preview outcome unknown: {ExceptionType}", ex.GetType().Name); }
            return;
        }
        if (stored.Outcome != StoreOutcome.Stored
            && !(message.IsEdit && stored.Outcome == StoreOutcome.Updated && intake.Kind == "invalid")) return;
        if (intake.Kind == "list")
        {
            var items = await store.ListAsync(intake.Scope, ct);
            if (items.Count == 0)
                await client.SendTextAsync(message.ChatId, message.TopicId, "Проверок нет. " + ExpectationParser.Help, message.MessageId, ct);
            foreach (var e in items)
            {
                var text = $"{e.Id:N}\n{e.Subject}; {e.EventType}; {e.Status}. "
                    + $"Ежедневно {ExpectationTimePolicy.Minute(e.DeadlineMinute)}, допуск {e.GraceMinutes} мин, "
                    + $"UTC{ExpectationTimePolicy.Offset(e.OffsetMinutes)}; действует с {e.EffectiveFrom:yyyy-MM-dd}.\n"
                    + $"Последний день: {e.LastDate:yyyy-MM-dd}; результат: {e.LastOutcome ?? "ещё не проверен"}. "
                    + "satisfied — запись найдена, последующее исправление не открывает день; "
                    + "dispatch-unknown — отправка могла не дойти и не повторяется."
                    + (e.SkippedFrom is null ? "" : $"\nПропущенные даты: {e.SkippedFrom:yyyy-MM-dd}–{e.SkippedThrough:yyyy-MM-dd}.")
                    + "\n/expect_edit, /expect_pause, /expect_resume, /expect_cancel + ID.";
                await client.SendTextAsync(message.ChatId, message.TopicId, text, message.MessageId, ct);
            }
            return;
        }
        await client.SendTextAsync(message.ChatId, message.TopicId, Result(intake.Result), message.MessageId, ct);
    }

    public async Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct)
    {
        var result = "unavailable";
        var parts = callback.Data.Split(':');
        if (bot.FamilyId is { } family && bot.Role is "health" or "vet" && callback.MessageId > 0
            && callback.MessageChatType is "private" or "group" or "supergroup" && parts.Length == 2
            && parts[0] is "exp_save" or "exp_cancel" && Guid.TryParseExact(parts[1], "N", out var id))
        {
            var scope = new ReminderScope(family, bot.BotDbId, bot.TelegramBotId, bot.Role,
                callback.MessageChatId, callback.MessageTopicId, callback.MessageChatType, callback.FromUserId);
            try { result = await store.ResolveAsync(scope, id, callback.MessageId, parts[0] == "exp_save", ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ReminderPersistenceException.IsRetryable(ex))
            { throw new ReminderPersistenceException(); }
        }
        await client.AnswerCallbackAsync(callback.CallbackQueryId, Result(result), ct);
    }
}
