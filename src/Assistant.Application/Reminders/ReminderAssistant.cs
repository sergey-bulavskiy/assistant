using System.Globalization;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Reminders;

public sealed class ReminderAssistant(IReminderStore store, IClock clock, ICurrentFamily family,
    ILogger<ReminderAssistant> logger) : IReminderAssistant
{
    private static ReminderScope Scope(ReceivingBot bot, IncomingMessage m) => new(bot.FamilyId!.Value,
        bot.BotDbId, bot.TelegramBotId, bot.Role, m.ChatId, m.TopicId, m.ChatType, m.UserId!.Value);
    private static string Offset(int minutes) => (minutes < 0 ? "-" : "+")
        + TimeSpan.FromMinutes(Math.Abs(minutes)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    private static string Minute(int minute) => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)).ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string SettingsText(ReminderPreferences p) => $"Фиксированное смещение UTC{Offset(p.OffsetMinutes)}, без перехода на летнее время. Тихие часы {Minute(p.QuietStartMinute)}–{Minute(p.QuietEndMinute)}. Лимит: 10 начатых отправок за день UTC на роль всей семьи.";
    private static string StatusText(string status) => status switch
    {
        "draft" => "ожидает сохранения", "active" => "запланировано", "sent" => "отправлено",
        "cancelled" => "отменено", "skipped" => "пропущено", "unknown" => "результат отправки неизвестен",
        _ => "состояние недоступно"
    };
    private static string OutcomeText(string? outcome) => outcome switch
    {
        null => "не начата", "sent" => "отправлено", "skipped" => "пропущено",
        "unknown" => "результат неизвестен (могла не дойти; автоматически не повторяется)",
        _ => "результат недоступен"
    };
    public static string ItemText(ReminderItem r) => $"{r.Text}\n{r.DueAt.ToOffset(TimeSpan.FromMinutes(r.OffsetMinutes)):yyyy-MM-dd HH:mm} UTC{Offset(r.OffsetMinutes)}"
        + (r.DailyMinute == null ? "; один раз" : "; ежедневно")
        + $"; {StatusText(r.Status)}; последняя отправка: {OutcomeText(r.LastOutcome)}";
    public async Task<ReminderIntake?> AdmitAsync(ReceivingBot bot, IncomingMessage message, bool replyToAll, CancellationToken ct)
    {
        if (bot.FamilyId == null || bot.Role is not ("general" or "health" or "vet")
            || message.Kind != MessageKind.Text || message.Text == null) return null;
        var text = message.Text;
        var command = CommandParser.Parse(text, bot.Username);
        var conversational = ReminderParser.LooksConversational(text);
        if (command is not ("remind" or "reminders" or "reminder_settings")
            && (!conversational || !replyToAll && !Addressing.IsAddressed(bot, message, text))) return null;
        if (message.UserId is not { } actor || actor <= 0 || message.ChatType == "private"
            && (message.ChatId != actor || message.TopicId != null))
            return new(new(bot.FamilyId.Value, bot.BotDbId, bot.TelegramBotId, bot.Role,
                message.ChatId, message.TopicId, message.ChatType, message.UserId ?? 0), "invalid", Error: "У вас нет прав.");
        var s = Scope(bot, message);
        if (message.IsEdit) return new(s, "invalid", Error: "Изменения не меняют напоминания. Отмените старое и отправьте новый запрос.");
        try
        {
            var preferences = await store.GetPreferencesAsync(s, ct);
            var parsed = ReminderParser.Parse(text, bot.Username, clock.UtcNow, preferences);
            if (parsed.Kind == "create")
            {
                var admitted = await store.AdmitAsync(s, message.MessageId, parsed.Request!, ct);
                return new(s, "create", admitted?.Id, admitted == null ? "Не создано: предел напоминаний или время уже прошло." : null);
            }
            if (parsed.Kind == "set_settings") await store.SetPreferencesAsync(s, message.MessageId, parsed.Preferences!, ct);
            return new(s, parsed.Kind, Error: parsed.Error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ReminderPersistenceException.IsRetryable(ex))
        {
            throw new ReminderPersistenceException();
        }
    }
    public async Task HandleAsync(ReminderIntake intake, ITelegramClient client, IncomingMessage message, StoreResult stored, CancellationToken ct)
    {
        if (intake.Kind == "create" && intake.Id is { } id)
        {
            var row = await store.BeginPreviewAsync(intake.Scope, id, ct);
            if (row == null) return;
            var preferences = await store.GetPreferencesAsync(intake.Scope, ct);
            var text = "Предварительный просмотр. Ничего не запланировано до Сохранить.\n"
                + ItemText(row) + "\nМесто: текущий чат и эта тема.\n" + SettingsText(preferences)
                + "\nИзменение часового смещения не сдвигает уже созданные напоминания. /reminder_settings — настройки.";
            try
            {
                var preview = await client.SendTextWithButtonsAsync(message.ChatId, message.TopicId, text,
                    [new("Сохранить", $"rem_save:{id:N}"), new("Отмена", $"rem_cancel:{id:N}")], message.MessageId, ct);
                await store.BindPreviewAsync(intake.Scope, id, preview, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Reminder preview outcome unknown: {ExceptionType}", ex.GetType().Name);
            }
            return;
        }
        if (stored.Outcome != StoreOutcome.Stored
            && !(message.IsEdit && stored.Outcome == StoreOutcome.Updated && intake.Kind == "invalid")) return;
        if (intake.Kind == "list")
        {
            if (intake.Scope.Role is "health" or "vet")
                await client.SendTextAsync(message.ChatId, message.TopicId, "Проверки наличия записей: /expectations. Общий лимит отправок с напоминаниями.", message.MessageId, ct);
            var items = await store.ListAsync(intake.Scope, ct);
            await client.SendTextAsync(message.ChatId, message.TopicId,
                SettingsText(await store.GetPreferencesAsync(intake.Scope, ct)), message.MessageId, ct);
            if (items.Count == 0) await client.SendTextAsync(message.ChatId, message.TopicId, "Напоминаний нет.", message.MessageId, ct);
            foreach (var row in items)
                await client.SendTextWithButtonsAsync(message.ChatId, message.TopicId, ItemText(row),
                    row.Status is "draft" or "active" ? [new("Отмена", $"rem_cancel:{row.Id:N}")] : [], message.MessageId, ct);
            return;
        }
        var response = intake.Error ?? (intake.Kind is "settings" or "set_settings"
            ? SettingsText(await store.GetPreferencesAsync(intake.Scope, ct)) : ReminderParser.Help);
        await client.SendTextAsync(message.ChatId, message.TopicId, response, message.MessageId, ct);
    }
    public async Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct)
    {
        if (bot.FamilyId == null || callback.MessageId <= 0 || callback.MessageChatType is not ("private" or "group" or "supergroup"))
        {
            await client.AnswerCallbackAsync(callback.CallbackQueryId, "Недоступно.", ct); return;
        }
        var parts = callback.Data.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            await client.AnswerCallbackAsync(callback.CallbackQueryId, "Недоступно.", ct); return;
        }
        var scope = new ReminderScope(bot.FamilyId.Value, bot.BotDbId, bot.TelegramBotId, bot.Role,
            callback.MessageChatId, callback.MessageTopicId, callback.MessageChatType, callback.FromUserId);
        string result;
        try
        {
            result = parts[0] == "rem_save" ? await store.SaveAsync(scope, id, callback.MessageId, ct)
                : parts[0] == "rem_cancel" ? await store.CancelAsync(scope, id, ct) : "unavailable";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ReminderPersistenceException.IsRetryable(ex))
        {
            throw new ReminderPersistenceException();
        }
        var response = result switch
        {
            "saved" => "Напоминание сохранено.", "cancelled" => "Напоминание отменено.",
            "cancelled_started" => "Будущие отправки отменены; начатая отправка могла уже уйти.",
            "expired" => "Время прошло или предварительный просмотр истёк. Создайте новый запрос.",
            _ => "Недоступно."
        };
        await client.AnswerCallbackAsync(callback.CallbackQueryId, response, ct);
    }
    public async Task TickAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (bot.FamilyId == null || bot.Role is not ("general" or "health" or "vet")) return;
        family.Set(bot.FamilyId);
        await store.CleanupAsync(bot, ct);
        for (var i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            var dispatch = await store.ClaimDueAsync(bot, ct);
            if (dispatch == null) break;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var sent = await client.SendTextAsync(dispatch.ChatId, dispatch.TopicId, dispatch.Text, null, timeout.Token);
                // Do not retry the external send if completion persistence fails.
                await store.CompleteAsync(bot, dispatch, sent, timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Reminder delivery outcome unknown: {ExceptionType}", ex.GetType().Name);
            }
        }
    }
}
public sealed class ReminderPersistenceException : Exception
{
    public ReminderPersistenceException() : base("Reminder persistence failed.") { }
    public static bool IsRetryable(Exception ex) => ex is System.Data.Common.DbException { IsTransient: true }
        or IOException or TimeoutException || ex.InnerException is { } inner && IsRetryable(inner);
}
