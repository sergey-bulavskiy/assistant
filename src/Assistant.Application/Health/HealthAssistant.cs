using System.Globalization;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>The `health` role bot: a health tracking assistant for one household member (one profile
/// per bot, created lazily with the default safety rules). Answers its deterministic commands and
/// turns every other new text message into health events with one `fast` LLM call (JSON answer,
/// strict parser, code-side validation), marks recorded messages with ✍ and asks once when something
/// cannot be recorded. Edits are ignored. Never logs message text or model answers.</summary>
public class HealthAssistant : IHealthAssistant
{
    public const string OwnerOnlyText = "Только владелец семьи может менять профиль.";

    public const string NonTextText = "Голосовые и фото пока не поддерживаются — напишите текстом.";

    /// <summary>✍ (U+270D, no variation selector: the form Telegram allows for bots).</summary>
    public const string RecordedReaction = "✍";

    /// <summary>👍, tried once when the chat refuses ✍.</summary>
    public const string FallbackReaction = "\U0001F44D";

    private const string ExtractInstructionsFile = "extract.md";

    private const int MaxPhoneLength = 100;
    private const int MaxNoteLength = 500;

    private static readonly string[] StartDateFormats = { "dd.MM.yyyy", "d.M.yyyy" };

    private const string StartText =
        "Привет! Я веду дневник здоровья одного участника семьи. Пишите показатели обычным текстом " +
        "(например, «сахар 5.6 натощак» или «давление 120/80») — я запишу их и поставлю ✍ на сообщение. " +
        "Значения я пока не проверяю и предупреждений не отправляю: не полагайтесь на меня, " +
        "если самочувствие вызывает тревогу, — звоните врачу или в скорую. " +
        "Я никогда не советую лекарства и их дозы.\n" +
        "/today — записи за сегодня\n" +
        "/undo — отменить вашу последнюю запись\n" +
        "/del — удалить записи (в ответ на сообщение) или /del <номер>\n" +
        "/week — текущая неделя\n" +
        "/profile — профиль\n" +
        "/thresholds — пороги\n" +
        "/version — версия\n" +
        "Владелец семьи: /setstart ДД.ММ.ГГГГ — начало отсчёта, /settz Area/City — часовой пояс, " +
        "/setphone — телефон для экстренных случаев, /setnote — заметка с контекстом для ответов на вопросы, " +
        "/threshold — пороги от врача.";

    private const string ThresholdsHeader = "Пороги (глюкоза в ммоль/л, давление в мм рт. ст.):";

    private const string ThresholdsFooter =
        "Изменить (владелец): /threshold <правило> <поле> <значение> — вводите значения, которые дал врач. " +
        "Вернуть по умолчанию: /threshold <правило> default.";

    private const string SetStartUsageText = "Укажите дату начала отсчёта: /setstart ДД.ММ.ГГГГ";
    private const string SetStartRangeText = "Дата должна быть не позже сегодняшней и не раньше чем 300 дней назад.";
    private const string SetTimeZoneUsageText = "Укажите часовой пояс: /settz Area/City, например /settz Europe/Berlin";
    private const string SetPhoneUsageText = "Укажите номер для экстренных случаев: /setphone <текст> (до 100 символов).";
    private const string SetNoteUsageText = "Укажите заметку (до 500 символов): /setnote <текст>; /setnote - удаляет её.";

    private const string ThresholdUsageText =
        "Формат: /threshold <правило> <поле> <значение> — вводите значения, которые дал врач; " +
        "/threshold <правило> default — вернуть значения по умолчанию. Правила и поля: /thresholds.";

    private const string TodayEmptyText = "Сегодня записей нет.";
    private const string NothingToUndoText = "Нечего отменять.";
    private const string EventNotFoundText = "Не нашёл такую запись.";
    private const string DeleteUsageText =
        "Формат: /del в ответ на сообщение с показателями или /del <номер записи> (номера — в /today).";

    private static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    private readonly IHealthProfileStore _profiles;
    private readonly IFamilyOwnership _ownership;
    private readonly IEventStore _events;
    private readonly ILlmGateway _gateway;
    private readonly IRolePrompts _rolePrompts;
    private readonly FailureNoticeThrottle _failureNotices;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<HealthAssistant> _logger;

    public HealthAssistant(
        IHealthProfileStore profiles,
        IFamilyOwnership ownership,
        IEventStore events,
        ILlmGateway gateway,
        IRolePrompts rolePrompts,
        FailureNoticeThrottle failureNotices,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<HealthAssistant> logger)
    {
        _profiles = profiles;
        _ownership = ownership;
        _events = events;
        _gateway = gateway;
        _rolePrompts = rolePrompts;
        _failureNotices = failureNotices;
        _clock = clock;
        _buildInfo = buildInfo;
        _logger = logger;
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken)
    {
        // Only new messages. Edits (Updated) keep today's storage behaviour and nothing else for now;
        // redeliveries never reply twice.
        if (storeResult.Outcome != StoreOutcome.Stored || message.IsEdit || bot.FamilyId is not { } familyId)
        {
            return;
        }

        if (message.Kind == MessageKind.Service)
        {
            return;
        }

        if (message.Kind != MessageKind.Text || message.Text is not { } text)
        {
            // Voice and photos are not supported yet: say so in private chats, stay silent in groups.
            if (message.ChatType == "private")
            {
                await ReplyAsync(telegramClient, message, NonTextText, cancellationToken);
            }

            return;
        }

        // The profile (with its default rules) is created lazily by the bot's first text message.
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);

        var command = CommandParser.Parse(text, bot.Username);
        if (command is null)
        {
            // /cmd@otherbot (or any other slash text) is silent; everything else may hold readings.
            if (!text.StartsWith('/'))
            {
                await ExtractAsync(bot, telegramClient, message, text, familyId, profile, storeResult, cancellationToken);
            }

            return;
        }

        var args = CommandParser.ParseArgs(text);
        switch (command)
        {
            case "start" when message.ChatType == "private":
                await ReplyAsync(telegramClient, message, StartText, cancellationToken);
                return;

            case "week":
                await ReplyAsync(telegramClient, message, $"Неделя: {CurrentWeek(profile).Describe()}", cancellationToken);
                return;

            case "profile":
                await ReplyAsync(telegramClient, message, await DescribeProfileAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "thresholds":
                await ReplyAsync(telegramClient, message, await DescribeThresholdsAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "today":
                await ReplyAsync(telegramClient, message, await DescribeTodayAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "undo":
                await UndoAsync(bot, telegramClient, message, familyId, profile, cancellationToken);
                return;

            case "del":
                await DeleteAsync(bot, telegramClient, message, familyId, profile, args, cancellationToken);
                return;

            case "version":
                await ReplyAsync(telegramClient, message, VersionText.Format(_buildInfo, _clock.UtcNow), cancellationToken);
                return;

            case "setstart":
            case "settz":
            case "setphone":
            case "setnote":
            case "threshold":
                await HandleOwnerCommandAsync(command, familyId, profile, telegramClient, message, args, cancellationToken);
                return;

            default:
                return; // Unknown command, or /start in a group: silent.
        }
    }

    private async Task HandleOwnerCommandAsync(
        string command, long familyId, HealthProfileInfo profile, ITelegramClient telegramClient, IncomingMessage message, string? args,
        CancellationToken cancellationToken)
    {
        // Re-read on every command; checked before the arguments, so a non-owner learns nothing.
        if (message.UserId is not { } userId || !await _ownership.IsApprovedOwnerAsync(familyId, userId, cancellationToken))
        {
            await ReplyAsync(telegramClient, message, OwnerOnlyText, cancellationToken);
            return;
        }

        var reply = command switch
        {
            "setstart" => await SetStartAsync(familyId, profile, args, userId, cancellationToken),
            "settz" => await SetTimeZoneAsync(familyId, profile, args, userId, cancellationToken),
            "setphone" => await SetPhoneAsync(familyId, profile, args, userId, cancellationToken),
            "setnote" => await SetNoteAsync(familyId, profile, args, userId, cancellationToken),
            _ => await SetThresholdAsync(familyId, profile, args, userId, cancellationToken)
        };
        await ReplyAsync(telegramClient, message, reply, cancellationToken);
    }

    private StageWeekResult CurrentWeek(HealthProfileInfo profile) =>
        StageWeek.Compute(ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone), profile.StageStartDate);

    private async Task<string> DescribeProfileAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        var doctorRules = rules.Count(r => r.Source == SafetyRuleSources.Doctor);
        var start = profile.StageStartDate is { } date
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : "не задано (/setstart)";
        var note = string.IsNullOrEmpty(profile.ContextNote)
            ? "не задана (/setnote) — без неё ответы на вопросы не знают контекста"
            : profile.ContextNote;
        return "Профиль:\n" +
               $"Начало отсчёта: {start}\n" +
               $"Неделя: {CurrentWeek(profile).Describe()}\n" +
               $"Часовой пояс: {profile.TimeZone}\n" +
               $"Телефон для экстренных случаев: {profile.EmergencyPhone}\n" +
               $"Заметка: {note}\n" +
               $"Пороги: правил {rules.Count}, от врача {doctorRules} (/thresholds)";
    }

    private async Task<string> DescribeThresholdsAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        return $"{ThresholdsHeader}\n{string.Join("\n", rules.Select(SafetyRuleText.Format))}\n\n{ThresholdsFooter}";
    }

    private async Task<string> SetStartAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null || !DateOnly.TryParseExact(args, StartDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return SetStartUsageText;
        }

        var week = StageWeek.Compute(ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone), start);
        if (week.Status != StageWeekStatus.Valid)
        {
            return SetStartRangeText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { StageStartDate = start }, userId, cancellationToken);
        return $"Начало отсчёта: {start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}. Неделя: {week.Describe()}";
    }

    private async Task<string> SetTimeZoneAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null)
        {
            return SetTimeZoneUsageText;
        }

        if (!ProfileTimeZone.TryNormalize(args, out var zoneId))
        {
            return $"Неизвестный часовой пояс: {args}. {SetTimeZoneUsageText}";
        }

        await _profiles.SaveProfileAsync(familyId, profile with { TimeZone = zoneId }, userId, cancellationToken);
        return $"Часовой пояс: {zoneId}.";
    }

    private async Task<string> SetPhoneAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null || args.Length > MaxPhoneLength)
        {
            return SetPhoneUsageText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { EmergencyPhone = args }, userId, cancellationToken);
        return $"Телефон для экстренных случаев: {args}";
    }

    private async Task<string> SetNoteAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null)
        {
            return SetNoteUsageText;
        }

        if (args == "-")
        {
            await _profiles.SaveProfileAsync(familyId, profile with { ContextNote = null }, userId, cancellationToken);
            return "Заметка удалена.";
        }

        if (args.Length > MaxNoteLength)
        {
            return SetNoteUsageText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { ContextNote = args }, userId, cancellationToken);
        return "Заметка сохранена.";
    }

    private async Task<string> SetThresholdAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        var parts = args?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        if (parts.Length is not (2 or 3))
        {
            return ThresholdUsageText;
        }

        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        var current = rules.FirstOrDefault(r => string.Equals(r.RuleKey, parts[0], StringComparison.OrdinalIgnoreCase));
        var seed = current is null ? null : SafetyRuleDefaults.Find(current.RuleKey);
        if (current is null || seed is null)
        {
            return $"Нет такого правила: {parts[0]}. Список: /thresholds.";
        }

        if (parts.Length == 2)
        {
            if (!string.Equals(parts[1], "default", StringComparison.OrdinalIgnoreCase))
            {
                return ThresholdUsageText;
            }

            await _profiles.SaveRuleAsync(familyId, profile.Id, seed, userId, cancellationToken);
            return $"Восстановлены значения по умолчанию: {SafetyRuleText.Format(seed)}";
        }

        var edit = SafetyRuleEditor.SetField(current, parts[1], parts[2]);
        if (edit.Rule is null)
        {
            return edit.Error ?? ThresholdUsageText;
        }

        await _profiles.SaveRuleAsync(familyId, profile.Id, edit.Rule, userId, cancellationToken);
        return $"Сохранено: {SafetyRuleText.Format(edit.Rule)}";
    }

    private async Task<string> DescribeTodayAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var today = ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone);
        var from = ProfileTimeZone.StartOfDayUtc(today, profile.TimeZone);
        var to = ProfileTimeZone.StartOfDayUtc(today.AddDays(1), profile.TimeZone);
        var events = await _events.GetActiveAsync(familyId, profile.Id, from, to, cancellationToken);
        if (events.Count == 0)
        {
            return TodayEmptyText;
        }

        var zone = ProfileTimeZone.Find(profile.TimeZone);
        return $"Сегодня, {today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}:\n" +
               string.Join("\n", events.Select(e => HealthEventText.Line(e, zone)));
    }

    private async Task UndoAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        CancellationToken cancellationToken)
    {
        var deleted = message.UserId is { } userId
            ? await _events.DeleteLatestOfUserAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, message.TopicId, userId, _clock.UtcNow - UndoWindow,
                EventDeleteReasons.Undo, cancellationToken)
            : DeletedEvents.None;
        if (deleted.Events.Count == 0)
        {
            await ReplyAsync(telegramClient, message, NothingToUndoText, cancellationToken);
            return;
        }

        await ClearReactionsAsync(telegramClient, deleted.MessagesWithoutEvents, cancellationToken);
        await ReplyAsync(telegramClient, message, DeletedText(deleted), cancellationToken);
    }

    private async Task DeleteAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile, string? args,
        CancellationToken cancellationToken)
    {
        DeletedEvents deleted;
        if (args is null)
        {
            // A reply to the forum topic root is how every topic message looks; it is not a reply.
            var isReplyToTopicRoot = message.TopicId is { } topicId && message.ReplyToMessageId == topicId;
            if (message.ReplyToMessageId is not { } repliedTo || isReplyToTopicRoot)
            {
                await ReplyAsync(telegramClient, message, DeleteUsageText, cancellationToken);
                return;
            }

            deleted = await _events.DeleteBySourceTelegramMessageAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, repliedTo, EventDeleteReasons.Del, cancellationToken);
        }
        else
        {
            var idText = args.StartsWith('#') ? args[1..] : args;
            if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId) || eventId <= 0)
            {
                await ReplyAsync(telegramClient, message, DeleteUsageText, cancellationToken);
                return;
            }

            deleted = await _events.DeleteByIdAsync(familyId, profile.Id, eventId, EventDeleteReasons.Del, cancellationToken);
        }

        if (deleted.Events.Count == 0)
        {
            await ReplyAsync(telegramClient, message, EventNotFoundText, cancellationToken);
            return;
        }

        await ClearReactionsAsync(telegramClient, deleted.MessagesWithoutEvents, cancellationToken);
        await ReplyAsync(telegramClient, message, DeletedText(deleted), cancellationToken);
    }

    private static string DeletedText(DeletedEvents deleted) =>
        "Удалено: " + string.Join("; ", deleted.Events.Select(e => $"#{e.Id.ToString(CultureInfo.InvariantCulture)} {HealthEventText.Describe(e)}")) + ".";

    // A message whose every event is gone loses its ✍. Deleting never "un-sends" anything else.
    private async Task ClearReactionsAsync(ITelegramClient telegramClient, IReadOnlyList<MessageRef> messages, CancellationToken cancellationToken)
    {
        foreach (var source in messages)
        {
            try
            {
                await telegramClient.SetReactionAsync(source.ChatId, source.TelegramMessageId, null, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("failed to clear a reaction: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private async Task ExtractAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        StoreResult storeResult, CancellationToken cancellationToken)
    {
        if (!ExtractionPrompt.ShouldExtract(text))
        {
            return;
        }

        var messageDbId = storeResult.MessageDbId;
        var instructions = _rolePrompts.Find(BotRoles.Health, ExtractInstructionsFile);
        if (instructions is null)
        {
            // The prompt resource is missing (an Error was logged at startup): extraction is off and
            // the family is told that nothing was recorded.
            LogOutcome($"refused:{LlmRefusalReason.NotConfigured}", messageDbId, 0);
            await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
            return;
        }

        var request = new LlmRequest(
            familyId,
            bot.TelegramBotId,
            LlmConfig.FastTier,
            PreferredModel: null,
            ExtractionPrompt.BuildSystemPrompt(instructions, _clock.UtcNow, message.SentAt, profile.TimeZone),
            new[] { new LlmMessage(LlmMessageRole.User, text) },
            ChatId: message.ChatId,
            TopicId: message.TopicId,
            TriggerMessageId: messageDbId);

        LlmResult result;
        try
        {
            result = await _gateway.CompleteAsync(request, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("health extraction call failed: {ExceptionType}", ex.GetType().Name);
            LogOutcome("failed", messageDbId, 0);
            await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
            return;
        }

        if (!result.IsAnswer)
        {
            LogOutcome($"refused:{result.RefusalReason}", messageDbId, 0);
            await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
            return;
        }

        var output = ExtractionParser.Parse(result.Text);
        if (output is null)
        {
            LogOutcome("invalid_output", messageDbId, 0);
            await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
            return;
        }

        var valid = new List<NewHealthEvent>();
        var problems = new List<ExtractedUnclear>(output.Unclear);
        foreach (var extracted in output.Events)
        {
            var validation = HealthEventValidator.Validate(extracted, message.SentAt, profile.TimeZone);
            if (validation.Event is { } recordable)
            {
                valid.Add(recordable);
            }
            else if (validation.Problem is { } problem)
            {
                problems.Add(problem);
            }
        }

        if (valid.Count > 0)
        {
            var source = new HealthEventSource(messageDbId, bot.TelegramBotId, message.ChatId, message.TopicId, message.UserId);
            try
            {
                await _events.AddAsync(familyId, profile.Id, source, valid, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Tell the family nothing was recorded, then rethrow: the update is not consumed silently.
                _logger.LogError("saving health events failed: {ExceptionType}", ex.GetType().Name);
                LogOutcome("store_failed", messageDbId, 0);
                await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
                throw;
            }

            await MarkRecordedAsync(telegramClient, message, cancellationToken);
        }

        if (problems.Count > 0)
        {
            // One clarification per message, about the first problem; valid events stay recorded.
            await ReplyAsync(telegramClient, message, ExtractionReplies.Clarification(problems[0], text), cancellationToken, quote: true);
        }

        LogOutcome(valid.Count > 0 ? "events" : problems.Count > 0 ? "clarify" : "no_events", messageDbId, valid.Count);
    }

    // One line per extraction, never the text, the model answer or a fragment.
    private void LogOutcome(string outcome, long? messageDbId, int eventCount) =>
        _logger.LogInformation("Extraction {Outcome} for message {MessageDbId}: {EventCount} events", outcome, messageDbId, eventCount);

    private async Task SendFailureNoticeAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, CancellationToken cancellationToken)
    {
        if (!_failureNotices.TryAcquire(bot.TelegramBotId, message.ChatId, message.TopicId, _clock.UtcNow))
        {
            _logger.LogInformation("Extraction failure notice throttled for chat message {MessageId}", message.MessageId);
            return;
        }

        await ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
    }

    // ✍, or 👍 once if the chat refuses ✍. A failed reaction never undoes the recorded events.
    private async Task MarkRecordedAsync(ITelegramClient telegramClient, IncomingMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await telegramClient.SetReactionAsync(message.ChatId, message.MessageId, RecordedReaction, cancellationToken);
            return;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("recorded reaction refused, trying the fallback: {ExceptionType}", ex.GetType().Name);
        }

        try
        {
            await telegramClient.SetReactionAsync(message.ChatId, message.MessageId, FallbackReaction, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("failed to set the recorded reaction: {ExceptionType}", ex.GetType().Name);
        }
    }

    // Command replies follow the General assistant: a Telegram reply in groups, a plain message in
    // private chats. Clarifications and the failure notice always quote the message (quote: true),
    // in private chats too. Fixed bot texts, so never stored as conversation.
    private async Task ReplyAsync(
        ITelegramClient telegramClient, IncomingMessage message, string text, CancellationToken cancellationToken, bool quote = false)
    {
        try
        {
            var replyToMessageId = quote || message.ChatType != "private" ? message.MessageId : (int?)null;
            await telegramClient.SendTextAsync(message.ChatId, message.TopicId, text, replyToMessageId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("failed to send health reply: {ExceptionType}", ex.GetType().Name);
        }
    }
}
