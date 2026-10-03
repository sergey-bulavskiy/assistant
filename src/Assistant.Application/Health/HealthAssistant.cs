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
/// strict parser, code-side validation), checks every new event with the safety rules (flags saved with
/// it, one fixed alert per dangerous event), marks recorded messages with ✍ and asks once when something
/// cannot be recorded. When extraction fails or is refused, or the model missed a glucose or blood
/// pressure reading, a quick scan still alerts on dangerous values (or asks about implausible ones).
/// An edited text message (sent at most 24 h ago) is read again and its records follow the new text;
/// unchanged records keep their id, so they never alert twice. A question addressed to the bot that
/// records nothing gets one `smart` answer with the profile's context; it passes the dose-advice filter
/// and ends with a fixed footer. Never logs message text, model answers or values.</summary>
public class HealthAssistant : IHealthAssistant
{
    public const string OwnerOnlyText = "Только владелец семьи может менять профиль.";

    public const string NonTextText = "Голосовые и фото пока не поддерживаются — напишите текстом.";

    /// <summary>✍ (U+270D, no variation selector: the form Telegram allows for bots).</summary>
    public const string RecordedReaction = "✍";

    /// <summary>👍, tried once when the chat refuses ✍.</summary>
    public const string FallbackReaction = "\U0001F44D";

    private const string ExtractInstructionsFile = "extract.md";

    private const string AnswerInstructionsFile = "prompt.md";

    /// <summary>Ends every answer that came from the model (also one replaced by the dose filter).</summary>
    public const string AnswerFooter = "Не заменяю врача.";

    private const int MaxPhoneLength = 100;
    private const int MaxNoteLength = 500;

    private static readonly string[] StartDateFormats = { "dd.MM.yyyy", "d.M.yyyy" };

    private const string StartText =
        "Привет! Я веду дневник здоровья одного участника семьи. Пишите показатели обычным текстом " +
        "(например, «сахар 5.6 натощак» или «давление 120/80») — я запишу их и поставлю ✍ на сообщение. " +
        "Опасные значения и симптомы я сразу отмечаю фиксированным предупреждением по порогам из /thresholds " +
        "(пока врач их не подтвердил, они помечены «не подтверждено врачом»). Я не заменяю врача: " +
        "если самочувствие вызывает тревогу, звоните врачу или в скорую, не дожидаясь меня. " +
        "Я никогда не советую лекарства и их дозы. Можно задать вопрос: в личном чате просто напишите его, " +
        "в группе — упомяните меня или ответьте на моё сообщение.\n" +
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

    /// <summary>Edits of messages sent longer ago than this are ignored (their records stay).</summary>
    private static readonly TimeSpan EditWindow = TimeSpan.FromHours(24);

    private readonly IHealthProfileStore _profiles;
    private readonly IFamilyOwnership _ownership;
    private readonly IEventStore _events;
    private readonly ISafetyAlertStore _safetyAlerts;
    private readonly IMessageStore _messages;
    private readonly ILlmGateway _gateway;
    private readonly LlmConfig? _config;
    private readonly IRolePrompts _rolePrompts;
    private readonly FailureNoticeThrottle _failureNotices;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<HealthAssistant> _logger;

    public HealthAssistant(
        IHealthProfileStore profiles,
        IFamilyOwnership ownership,
        IEventStore events,
        ISafetyAlertStore safetyAlerts,
        IMessageStore messages,
        ILlmGateway gateway,
        LlmConfig? config,
        IRolePrompts rolePrompts,
        FailureNoticeThrottle failureNotices,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<HealthAssistant> logger)
    {
        _profiles = profiles;
        _ownership = ownership;
        _events = events;
        _safetyAlerts = safetyAlerts;
        _messages = messages;
        _gateway = gateway;
        _config = config;
        _rolePrompts = rolePrompts;
        _failureNotices = failureNotices;
        _clock = clock;
        _buildInfo = buildInfo;
        _logger = logger;
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken)
    {
        if (bot.FamilyId is not { } familyId)
        {
            return;
        }

        // An edit is read again (Updated; or Stored when the bot never saw the original). Redeliveries
        // (AlreadyProcessed, Duplicate, OffsetOnly) never reply twice.
        if (message.IsEdit)
        {
            if (storeResult.Outcome is StoreOutcome.Stored or StoreOutcome.Updated)
            {
                await HandleEditAsync(bot, telegramClient, message, familyId, storeResult, cancellationToken);
            }

            return;
        }

        if (storeResult.Outcome != StoreOutcome.Stored)
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

    // An edited text message is read again and its records follow the new text. Ignored: non-text
    // edits, edits into a command (commands run only when sent) and edits of messages sent more than
    // EditWindow ago; their records stay. Everything else (rules, alerts, clarification, quick scan,
    // failure notice) works as for a new message.
    private async Task HandleEditAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, StoreResult storeResult,
        CancellationToken cancellationToken)
    {
        if (message.Kind != MessageKind.Text || message.Text is not { } text || text.StartsWith('/') || storeResult.MessageDbId is null)
        {
            return;
        }

        if (_clock.UtcNow - message.SentAt > EditWindow)
        {
            _logger.LogInformation("Edit of message {MessageDbId} ignored: sent too long ago", storeResult.MessageDbId);
            return;
        }

        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);
        await ExtractAsync(bot, telegramClient, message, text, familyId, profile, storeResult, cancellationToken, isEdit: true);
    }

    // An edit into a text without readings (too short or emoji only, no model call): the message's
    // records are deleted and its reaction is cleared.
    private async Task RemoveEditedEventsAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile, long? messageDbId,
        CancellationToken cancellationToken)
    {
        var source = new HealthEventSource(messageDbId, bot.TelegramBotId, message.ChatId, message.TopicId, message.UserId);
        var replaced = await _events.ReplaceMessageEventsAsync(familyId, profile.Id, source, Array.Empty<NewHealthEvent>(), cancellationToken);
        await UpdateEditReactionAsync(telegramClient, message, replaced, messageDbId, cancellationToken);
    }

    // An edited message keeps its reaction while it has records (no second call: a refused ✍ would
    // turn into 👍), gets ✍ when it had none before, and loses the reaction when none are left.
    // Logs counts only.
    private async Task UpdateEditReactionAsync(
        ITelegramClient telegramClient, IncomingMessage message, ReplacedEvents replaced, long? messageDbId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Edit of message {MessageDbId}: {Kept} kept, {Deleted} deleted, {Added} added",
            messageDbId, replaced.KeptCount, replaced.Deleted.Count, replaced.Events.Count - replaced.KeptCount);
        if (replaced.Events.Count > 0 && !replaced.HadEvents)
        {
            await MarkRecordedAsync(telegramClient, message, cancellationToken);
        }
        else if (replaced.Events.Count == 0 && replaced.HadEvents)
        {
            await ClearReactionsAsync(telegramClient, new[] { new MessageRef(message.ChatId, message.MessageId) }, cancellationToken);
        }
    }

    private async Task ExtractAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        StoreResult storeResult, CancellationToken cancellationToken, bool isEdit = false)
    {
        var messageDbId = storeResult.MessageDbId;
        if (!ExtractionPrompt.ShouldExtract(text))
        {
            // No model call. An edit into such a text has no readings left.
            if (isEdit)
            {
                await RemoveEditedEventsAsync(bot, telegramClient, message, familyId, profile, messageDbId, cancellationToken);
            }

            return;
        }

        var instructions = _rolePrompts.Find(BotRoles.Health, ExtractInstructionsFile);
        if (instructions is null)
        {
            // The prompt resource is missing (an Error was logged at startup): extraction is off and
            // the family is told that nothing was recorded.
            LogOutcome($"refused:{LlmRefusalReason.NotConfigured}", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
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
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
            return;
        }

        if (!result.IsAnswer)
        {
            LogOutcome($"refused:{result.RefusalReason}", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
            return;
        }

        var output = ExtractionParser.Parse(result.Text);
        if (output is null)
        {
            LogOutcome("invalid_output", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
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

        // An edit replaces the message's records even when the new text has none left.
        if (valid.Count > 0 || isEdit)
        {
            var source = new HealthEventSource(messageDbId, bot.TelegramBotId, message.ChatId, message.TopicId, message.UserId);
            IReadOnlyList<SafetyEvaluation> evaluations = Array.Empty<SafetyEvaluation>();
            var evaluated = false;
            IReadOnlyList<HealthEventInfo> saved;
            ReplacedEvents? replaced = null;
            try
            {
                // Safety rules (deterministic code, never the model) run on every new event before it is
                // saved; their flags are saved with it. On an edit they run on every event of the new
                // text: an unchanged event keeps its id, so its existing alert claim stops a second alert.
                if (valid.Count > 0)
                {
                    var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
                    var recent = await LoadComboContextAsync(familyId, profile, valid, rules, messageDbId, cancellationToken);
                    evaluations = SafetyRuleEvaluator.Evaluate(valid, recent, rules, _clock.UtcNow);
                    evaluated = true;
                }

                var flagged = valid.Select((e, i) => e with { Flags = evaluations[i].Flags }).ToList();
                if (isEdit)
                {
                    replaced = await _events.ReplaceMessageEventsAsync(familyId, profile.Id, source, flagged, cancellationToken);
                    saved = replaced.Events;
                }
                else
                {
                    saved = await _events.AddAsync(familyId, profile.Id, source, flagged, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Tell the family nothing was recorded, then rethrow: the update is not consumed silently.
                // When the rules already found a dangerous value (only the save failed), its fixed alert
                // goes out instead of the plain notice; nothing is claimed, there is no event row. When the
                // rules could not be checked at all and the text holds a reading in a quick-scan format,
                // the notice is not throttled: the family always hears that a possible reading was missed.
                _logger.LogError("saving health events failed: {ExceptionType}", ex.GetType().Name);
                LogOutcome("store_failed", messageDbId, 0);
                if (SafetyRuleEvaluator.MostSevere(evaluations.Select(e => e.Alert)) is { } unsaved)
                {
                    await ReplyAsync(
                        telegramClient, message, SafetyAlertText.FormatNotRecorded(unsaved, profile.EmergencyPhone), cancellationToken, quote: true);
                    _logger.LogWarning(
                        "Safety alert {RuleKey} ({Level}) for unsaved message {MessageDbId}", unsaved.RuleKey, unsaved.Level, messageDbId);
                }
                else if (!evaluated && QuickReadingScanner.Scan(text).Count > 0)
                {
                    await ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
                }
                else
                {
                    await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken);
                }

                throw;
            }

            // ✍ first, then the alerts; the clarification (below) comes last.
            if (replaced is null)
            {
                await MarkRecordedAsync(telegramClient, message, cancellationToken);
            }
            else
            {
                await UpdateEditReactionAsync(telegramClient, message, replaced, messageDbId, cancellationToken);
            }
            await SendAlertsAsync(telegramClient, message, familyId, profile, saved, evaluations, messageDbId, cancellationToken);
        }

        if (problems.Count > 0)
        {
            // One clarification per message, about the first problem; valid events stay recorded.
            await ReplyAsync(telegramClient, message, ExtractionReplies.Clarification(problems[0], text), cancellationToken, quote: true);
        }

        var quickScanSent = false;
        if (output.Events.Count > 0 || output.Unclear.Count == 0)
        {
            // A reading in one of the quick-scan formats that the model missed (no event of that
            // metric in its answer, e.g. it recorded a weight but not "сахар 2.5") still gets its fixed
            // alert (nothing recorded for that value), or the clarification when it is not plausible
            // and no clarification was sent yet. Otherwise silent, no failure notice.
            var extractedTypes = output.Events
                .Select(e => e.Type?.Trim().ToLowerInvariant())
                .OfType<string>()
                .ToHashSet();
            quickScanSent = await TrySendQuickScanReplyAsync(
                telegramClient, message, text, familyId, profile, messageDbId, extractedTypes, clarify: problems.Count == 0, cancellationToken);
        }

        LogOutcome(valid.Count > 0 ? "events" : problems.Count > 0 ? "clarify" : "no_events", messageDbId, valid.Count);

        // An answer only for a new message addressed to this bot that the model marked as a question
        // and that produced nothing else: no record, no clarification, no quick-scan reply. A reading
        // is never also answered. reply_to_all is never read for this role.
        if (!isEdit && output.IsQuestion && valid.Count == 0 && problems.Count == 0 && !quickScanSent
            && Addressing.IsAddressed(bot, message, text))
        {
            await AnswerQuestionAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, cancellationToken);
        }
    }

    // Earlier active events the combination rule may pair with (deleted ones never count). Read before
    // the new events are saved, so a new event never pairs with itself through the store; the
    // message's own earlier events are left out too (an edit replaces them).
    private async Task<IReadOnlyList<HealthEventInfo>> LoadComboContextAsync(
        long familyId, HealthProfileInfo profile, IReadOnlyList<NewHealthEvent> events, IReadOnlyList<SafetyRuleInfo> rules,
        long? messageDbId, CancellationToken cancellationToken)
    {
        var combo = rules.FirstOrDefault(r => r.RuleKey == SafetyRuleKeys.ComboBpSymptoms);
        if (combo?.WindowHours is not { } hours || hours <= 0)
        {
            return Array.Empty<HealthEventInfo>();
        }

        var window = TimeSpan.FromHours(hours);
        var from = events.Min(e => e.OccurredAt) - window;
        // GetActiveAsync's upper bound is exclusive; the evaluator checks the exact window.
        var to = events.Max(e => e.OccurredAt) + window + TimeSpan.FromSeconds(1);
        var active = await _events.GetActiveAsync(familyId, profile.Id, from, to, cancellationToken);
        return active.Where(e => messageDbId is null || e.SourceMessageId != messageDbId).ToList();
    }

    // One fixed alert per saved event that reached an alert level, in event order: claimed in
    // safety_alerts first and sent only when this call won the claim, so a redelivery never alerts
    // twice. A failed claim still sends (a duplicate alert is better than a missing one). A failed
    // send is retried once in place; if that fails too it is logged as an Error (rule key and message
    // id only) and the claim row stays, so a redelivery does not send it either. Logs rule key and
    // level only, never values.
    private async Task SendAlertsAsync(
        ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        IReadOnlyList<HealthEventInfo> saved, IReadOnlyList<SafetyEvaluation> evaluations, long? messageDbId,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < saved.Count && i < evaluations.Count; i++)
        {
            if (evaluations[i].Alert is not { } decision)
            {
                continue;
            }

            var eventId = saved[i].Id;
            var alert = new NewSafetyAlert(
                eventId, decision.RuleKey, decision.Level, decision.Threshold, decision.ThresholdSource, message.ChatId, message.TopicId);
            bool claimed;
            try
            {
                claimed = await _safetyAlerts.TryClaimAsync(familyId, alert, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("safety alert claim failed, sending anyway: {ExceptionType}", ex.GetType().Name);
                claimed = true;
            }

            if (!claimed)
            {
                _logger.LogInformation("Safety alert {RuleKey} for event {EventId} not claimed", decision.RuleKey, eventId);
                continue;
            }

            var alertText = SafetyAlertText.Format(decision, profile.EmergencyPhone);
            if (!await ReplyAsync(telegramClient, message, alertText, cancellationToken, quote: true)
                && !await ReplyAsync(telegramClient, message, alertText, cancellationToken, quote: true))
            {
                _logger.LogError("Safety alert {RuleKey} for message {MessageDbId} could not be sent", decision.RuleKey, messageDbId);
                continue;
            }

            _logger.LogWarning("Safety alert {RuleKey} ({Level}) for event {EventId}", decision.RuleKey, decision.Level, eventId);
        }
    }

    // One smart call for an addressed question: roles/health/prompt.md plus the runtime block (stage
    // week, context note, thresholds, readings of the last 24 hours) and the last few messages of this
    // chat/topic. The answer passes the dose-advice filter (dose advice replaces the whole answer with
    // the fixed refusal) and always ends with the footer. A refusal or failure gets a fixed text, never
    // silence, and is not stored. Logs the outcome only, never the question, the note or the answer.
    private async Task AnswerQuestionAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        long? messageDbId, CancellationToken cancellationToken)
    {
        var instructions = _rolePrompts.Find(BotRoles.Health, AnswerInstructionsFile);
        if (_config is null || instructions is null)
        {
            LogAnswer($"refused:{LlmRefusalReason.NotConfigured}", messageDbId);
            await SendAnswerAsync(bot, telegramClient, message, GeneralAssistant.NotConfiguredText, storeAsContext: false, cancellationToken);
            return;
        }

        LlmResult result;
        try
        {
            var now = _clock.UtcNow;
            var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
            // Readings may be stated up to 10 minutes ahead of the message; the hour covers that.
            var readings = await _events.GetActiveAsync(
                familyId, profile.Id, now - ConsultationPrompt.ReadingsWindow, now.AddHours(1), cancellationToken);
            // No /new for this role: the last few messages of this chat/topic; the question itself is
            // excluded here and appended once by ContextBuilder.
            var history = await _messages.GetRecentContextAsync(
                bot.TelegramBotId, message.ChatId, message.TopicId, afterMessageId: null, beforeMessageId: messageDbId,
                Math.Min(_config.MaxContextMessages, ConsultationPrompt.MaxHistoryMessages), cancellationToken);
            var request = new LlmRequest(
                familyId,
                bot.TelegramBotId,
                LlmConfig.SmartTier,
                PreferredModel: null,
                ConsultationPrompt.BuildSystemPrompt(instructions, now, profile, CurrentWeek(profile), rules, readings),
                ContextBuilder.Build(history, text, message.Username, message.ChatType != "private", _config.MaxInputChars),
                ChatId: message.ChatId,
                TopicId: message.TopicId,
                TriggerMessageId: messageDbId);

            using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var typingTask = TypingIndicator.RunAsync(telegramClient, message.ChatId, message.TopicId, typingCts.Token);
            try
            {
                result = await _gateway.CompleteAsync(request, cancellationToken);
            }
            finally
            {
                await typingCts.CancelAsync();
                await typingTask;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("health answer failed: {ExceptionType}", ex.GetType().Name);
            LogAnswer("failed", messageDbId);
            await SendAnswerAsync(bot, telegramClient, message, GeneralAssistant.FailedText, storeAsContext: false, cancellationToken);
            return;
        }

        if (!result.IsAnswer)
        {
            LogAnswer($"refused:{result.RefusalReason}", messageDbId);
            await SendAnswerAsync(
                bot, telegramClient, message, GeneralAssistant.RefusalText(result, _clock.UtcNow), storeAsContext: false, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            LogAnswer("empty", messageDbId);
            await SendAnswerAsync(bot, telegramClient, message, GeneralAssistant.FailedText, storeAsContext: false, cancellationToken);
            return;
        }

        // The deterministic filter is the enforcement (D7); the prompt rule alone is not trusted. The
        // model's own text of a replaced answer is never sent, stored or logged.
        var blocked = DoseAdviceFilter.ContainsDoseAdvice(result.Text);
        var answer = blocked ? DoseAdviceFilter.RefusalText : result.Text.Trim();
        LogAnswer(blocked ? "dose_advice_replaced" : "answered", messageDbId);
        await SendAnswerAsync(bot, telegramClient, message, $"{answer}\n\n{AnswerFooter}", storeAsContext: true, cancellationToken);
    }

    // Like the General assistant's replies: split for Telegram; in groups the first part is a reply to
    // the question, private chats never quote. Only answers are stored as outgoing context.
    private async Task SendAnswerAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, bool storeAsContext,
        CancellationToken cancellationToken)
    {
        var isGroup = message.ChatType != "private";
        var isFirstPart = true;
        foreach (var part in ReplySplitter.Split(text))
        {
            int sentMessageId;
            try
            {
                var replyToMessageId = isGroup && isFirstPart ? message.MessageId : (int?)null;
                sentMessageId = await telegramClient.SendTextAsync(message.ChatId, message.TopicId, part, replyToMessageId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("failed to send health answer: {ExceptionType}", ex.GetType().Name);
                return;
            }

            isFirstPart = false;
            if (!storeAsContext)
            {
                continue;
            }

            try
            {
                // CancellationToken.None: the user already has this part, so a shutdown must not drop its record.
                await _messages.StoreOutgoingAsync(
                    bot.TelegramBotId, message.ChatId, message.TopicId, message.ChatType, sentMessageId, part, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("failed to store health answer: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private void LogAnswer(string outcome, long? messageDbId) =>
        _logger.LogInformation("Answer {Outcome} for message {MessageDbId}", outcome, messageDbId);

    // One line per extraction, never the text, the model answer or a fragment.
    private void LogOutcome(string outcome, long? messageDbId, int eventCount) =>
        _logger.LogInformation("Extraction {Outcome} for message {MessageDbId}: {EventCount} events", outcome, messageDbId, eventCount);

    // Extraction refused or failed (LLM off included): nothing is recorded. A dangerous reading in one
    // of the quick-scan formats still gets its fixed alert, an implausible one (e.g. another unit) the
    // clarification; otherwise the throttled failure notice.
    private async Task HandleExtractionFailureAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        long? messageDbId, bool isEdit, CancellationToken cancellationToken)
    {
        if (await TrySendQuickScanReplyAsync(
                telegramClient, message, text, familyId, profile, messageDbId, new HashSet<string>(), clarify: true, cancellationToken))
        {
            return;
        }

        // A failed edit always says so (the throttle is neither consulted nor used up): otherwise the
        // earlier records would silently stay and the user would think the edit was applied.
        await SendFailureNoticeAsync(bot, telegramClient, message, cancellationToken, throttled: !isEdit);
    }

    // Quick scan: transient, unsaved readings validated like extracted ones and checked against the
    // profile's rules with no earlier events (only glucose.any and the two pressure rules can fire).
    // Hits of a metric in skipTypes (the model extracted that metric) are ignored. One alert for the
    // most severe hit (no safety_alerts row: there is no event), then, when clarify is set, the
    // clarification for the first hit the validator rejected (e.g. "сахар 45": another unit). Neither
    // is throttled. Returns false when nothing was sent (a failed scan included); the caller decides
    // on a fallback. Logs rule key and level only.
    private async Task<bool> TrySendQuickScanReplyAsync(
        ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        long? messageDbId, IReadOnlySet<string> skipTypes, bool clarify, CancellationToken cancellationToken)
    {
        SafetyDecision? decision = null;
        ExtractedUnclear? problem = null;
        try
        {
            var readings = new List<NewHealthEvent>();
            foreach (var hit in QuickReadingScanner.Scan(text).Where(h => h.Type is null || !skipTypes.Contains(h.Type)))
            {
                var validation = HealthEventValidator.Validate(hit, message.SentAt, profile.TimeZone);
                if (validation.Event is { } reading)
                {
                    readings.Add(reading);
                }
                else
                {
                    problem ??= validation.Problem;
                }
            }

            if (readings.Count > 0)
            {
                var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
                var evaluations = SafetyRuleEvaluator.Evaluate(readings, Array.Empty<HealthEventInfo>(), rules, _clock.UtcNow);
                decision = SafetyRuleEvaluator.MostSevere(evaluations.Select(e => e.Alert));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("quick scan failed: {ExceptionType}", ex.GetType().Name);
            return false;
        }

        var sent = false;
        if (decision is not null)
        {
            await ReplyAsync(telegramClient, message, SafetyAlertText.FormatNotRecorded(decision, profile.EmergencyPhone), cancellationToken, quote: true);
            _logger.LogWarning("Quick scan alert {RuleKey} ({Level}) for message {MessageDbId}", decision.RuleKey, decision.Level, messageDbId);
            sent = true;
        }

        if (clarify && problem is not null)
        {
            await ReplyAsync(telegramClient, message, ExtractionReplies.Clarification(problem, text), cancellationToken, quote: true);
            _logger.LogInformation("Quick scan clarification for message {MessageDbId}", messageDbId);
            sent = true;
        }

        return sent;
    }

    private async Task SendFailureNoticeAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, CancellationToken cancellationToken, bool throttled = true)
    {
        if (throttled && !_failureNotices.TryAcquire(bot.TelegramBotId, message.ChatId, message.TopicId, _clock.UtcNow))
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
    // in private chats too. Fixed bot texts, so never stored as conversation. Returns false when the
    // send failed (already logged).
    private async Task<bool> ReplyAsync(
        ITelegramClient telegramClient, IncomingMessage message, string text, CancellationToken cancellationToken, bool quote = false)
    {
        try
        {
            var replyToMessageId = quote || message.ChatType != "private" ? message.MessageId : (int?)null;
            await telegramClient.SendTextAsync(message.ChatId, message.TopicId, text, replyToMessageId, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("failed to send health reply: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }
}
