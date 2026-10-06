using System.Globalization;
using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Llm;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Messages;

/// <summary>The `general` role bot (spec §2): decides whether to answer, handles its commands, asks
/// the LLM gateway and delivers (and stores) the reply. Never logs message or answer text.</summary>
public class GeneralAssistant : IGeneralAssistant
{
    public const string NotConfiguredText = "Ассистент пока не настроен.";

    private const string StartText =
        "Привет! Я отвечаю на вопросы с помощью модели. В личных сообщениях отвечаю на всё; " +
        "в группах — только если обратиться по имени или ответить на моё сообщение " +
        "(владелец может включить ответы на все сообщения группы или темы в /settings бота-менеджера). " +
        "/new — начать разговор заново. /model — выбрать модель. /tokens — расход токенов. /version — версия.";

    private const string NewConversationText = "Начинаем новый разговор.";

    private readonly IMessageStore _store;
    private readonly ILlmGateway _gateway;
    private readonly IChatSettingsStore _chatSettings;
    private readonly ILlmUsageQuery _usageQuery;
    private readonly LlmConfig? _config;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<GeneralAssistant> _logger;
    private readonly ITraceSession _trace;

    /// <param name="config">Null when LLM is off or its config is invalid: every question then gets
    /// <see cref="NotConfiguredText"/> without touching settings, context or the gateway.</param>
    public GeneralAssistant(
        IMessageStore store,
        ILlmGateway gateway,
        IChatSettingsStore chatSettings,
        ILlmUsageQuery usageQuery,
        LlmConfig? config,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<GeneralAssistant> logger,
        ITraceSession? trace = null)
    {
        _store = store;
        _gateway = gateway;
        _chatSettings = chatSettings;
        _usageQuery = usageQuery;
        _config = config;
        _clock = clock;
        _buildInfo = buildInfo;
        _logger = logger;
        _trace = trace ?? NullTraceSession.Instance;
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false)
    {
        if (storeResult.Outcome is StoreOutcome.AlreadyProcessed or StoreOutcome.Duplicate or StoreOutcome.OffsetOnly)
        {
            return;
        }

        if (message.Kind != MessageKind.Text || message.IsEdit || message.Text is not { } text || bot.FamilyId is not { } familyId)
        {
            // Service, non-text (photos/voice are M7) and edited messages are stored, never answered.
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", message.IsEdit ? "edit_suppressed" : "service_or_non_text"));
            return;
        }

        var command = CommandParser.Parse(text, bot.Username);
        if (command is not null)
        {
            await HandleCommandAsync(command, bot, telegramClient, message, familyId, storeResult, cancellationToken);
            return;
        }

        if (text.StartsWith('/'))
        {
            // Looks like a command but is not addressed to this bot (/cmd@otherbot): silent.
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "other_bot_command"));
            return;
        }

        if (!IsAddressed(bot, message, text, replyToAll, out var onlyByReplyToAll))
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "not_addressed"));
            return;
        }

        await AnswerAsync(bot, telegramClient, message, text, familyId, storeResult, silentRefusal: onlyByReplyToAll, cancellationToken);
    }

    private async Task HandleCommandAsync(
        string command, ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, StoreResult storeResult, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "start" when message.ChatType == "private":
                await ReplyAsync(bot, telegramClient, message, StartText, cancellationToken);
                return;

            case "new":
                // Spec §8.3: the cutoff is the /new command's own messages.id. A null MessageDbId
                // means the command itself was never stored (should not happen in practice, but
                // StoreResult.MessageDbId is nullable) -- refuse rather than claim a new conversation
                // started when the cutoff could not actually be recorded.
                if (storeResult.MessageDbId is not { } newCommandMessageId)
                {
                    _logger.LogError("/new command's own message has no stored id; refusing instead of claiming success");
                    await ReplyAsync(bot, telegramClient, message, FailedText, cancellationToken);
                    return;
                }

                await _chatSettings.SetContextStartMessageIdAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, newCommandMessageId, cancellationToken);
                await ReplyAsync(bot, telegramClient, message, NewConversationText, cancellationToken);
                return;

            case "model":
                await HandleModelCommandAsync(bot, telegramClient, message, familyId, cancellationToken);
                return;

            case "tokens":
                await HandleTokensCommandAsync(bot, telegramClient, message, familyId, cancellationToken);
                return;

            case "version":
                await ReplyAsync(bot, telegramClient, message, VersionText.Format(_buildInfo, _clock.UtcNow), cancellationToken);
                return;

            default:
                await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "unknown_command"));
                return; // Unknown command (or /start in a group): silent.
        }
    }

    private const string NoUsageText = "Пока нет данных. Считаются только вызовы после обновления.";

    private async Task HandleTokensCommandAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, CancellationToken cancellationToken)
    {
        var setting = await _chatSettings.GetAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, cancellationToken);
        var usage = await _usageQuery.GetChatUsageAsync(
            familyId, bot.TelegramBotId, message.ChatId, message.TopicId, setting.ContextStartMessageId, cancellationToken);
        await ReplyAsync(bot, telegramClient, message, DescribeUsage(usage, sinceNew: setting.ContextStartMessageId is not null), cancellationToken);
    }

    private static string DescribeUsage(LlmUsageSummary usage, bool sinceNew)
    {
        if (usage.Models.Count == 0)
        {
            return NoUsageText;
        }

        var header = sinceNew ? "Расход с последнего /new:" : "Расход в этом чате:";
        var models = string.Join(", ", usage.Models.Select(m => $"{m.Model} ({m.Calls})"));
        return $"{header}\nОтветов: {FormatNumber(usage.Calls)}\nВходящих токенов: {FormatNumber(usage.InputTokens)}\n" +
               $"Исходящих токенов: {FormatNumber(usage.OutputTokens)}\nМодели: {models}";
    }

    // 13345 -> "13 345": invariant digits, plain-space thousands separator (no culture surprises).
    private static string FormatNumber(long value) =>
        value.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

    private async Task HandleModelCommandAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, CancellationToken cancellationToken)
    {
        var models = _gateway.DescribeModels();
        if (_config is null || models.Count == 0)
        {
            await ReplyAsync(bot, telegramClient, message, NotConfiguredText, cancellationToken);
            return;
        }

        var arg = message.Text is null ? null : CommandParser.ParseArgs(message.Text);
        var current = await _chatSettings.GetAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, cancellationToken);

        if (arg is null)
        {
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, current.PreferredModel, _clock.UtcNow), cancellationToken);
            return;
        }

        if (string.Equals(arg, "auto", StringComparison.OrdinalIgnoreCase))
        {
            await _chatSettings.SetPreferredModelAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, null, cancellationToken);
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, null, _clock.UtcNow), cancellationToken);
            return;
        }

        // Store the catalog's own spelling of the name, so "/model HAIKU" and "/model haiku" agree.
        // Nit: match already comes from models (_gateway.DescribeModels()), so it is by construction
        // a name the gateway knows -- a further _gateway.IsKnownModel(match.Name) check was redundant.
        var match = models.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, current.PreferredModel, _clock.UtcNow), cancellationToken);
            return;
        }

        await _chatSettings.SetPreferredModelAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, match.Name, cancellationToken);
        await ReplyAsync(bot, telegramClient, message, DescribeModels(models, match.Name, _clock.UtcNow), cancellationToken);
    }

    private static string DescribeModels(IReadOnlyList<ModelStatus> models, string? preferred, DateTimeOffset now)
    {
        var lines = models.Select(m =>
        {
            var marker = string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase) ? " (текущая)" : string.Empty;
            var status = m.IsAvailable
                ? "доступна"
                : m.RetryAt is { } retryAt && IsKnownRetryTime(retryAt, now)
                    ? $"недоступна до {FormatTime(retryAt)} UTC"
                    : "недоступна";
            return $"- {m.Name}: {status}{marker}";
        });

        var current = preferred ?? "auto";
        return $"Модели:\n{string.Join("\n", lines)}\n\nНастройка этого чата: {current}. " +
               "/model <имя> — выбрать модель, /model auto — по умолчанию.";
    }

    private async Task AnswerAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, StoreResult storeResult,
        bool silentRefusal, CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "not_configured"));
            if (silentRefusal)
            {
                LogSilentRefusal(LlmRefusalReason.NotConfigured);
                return;
            }

            await ReplyAsync(bot, telegramClient, message, NotConfiguredText, cancellationToken);
            return;
        }

        var chatSetting = await _chatSettings.GetAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, cancellationToken);
        var isGroup = message.ChatType != "private";

        // The current message is already stored; beforeMessageId keeps it out of the history so
        // ContextBuilder appends it exactly once.
        var history = await _store.GetRecentContextAsync(
            bot.TelegramBotId, message.ChatId, message.TopicId,
            afterMessageId: chatSetting.ContextStartMessageId,
            beforeMessageId: storeResult.MessageDbId,
            _config.MaxContextMessages,
            cancellationToken);

        var llmMessages = ContextBuilder.Build(history, text, message.Username, isGroup, _config.MaxInputChars);
        var request = new LlmRequest(
            familyId, bot.TelegramBotId, LlmConfig.SmartTier, chatSetting.PreferredModel, BuildSystemPrompt(isGroup), llmMessages,
            ChatId: message.ChatId, TopicId: message.TopicId, TriggerMessageId: storeResult.MessageDbId);

        LlmResult result;
        using (var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
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

        if (result.IsAnswer && !string.IsNullOrEmpty(result.Text))
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("answer", "generated", "normal",
                AttemptId: result.TraceAttemptId, Text: result.Text));
            using (TracingTelegramClient.ForAttempt(result.TraceAttemptId))
            {
                await ReplyAsync(bot, telegramClient, message, result.Text, cancellationToken, storeAsContext: true);
            }
        }
        else if (silentRefusal)
        {
            // Answered only because of reply_to_all -- a refusal text in reply to every ordinary
            // group message would be noise.
            LogSilentRefusal(result.RefusalReason);
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", RefusalCode(result),
                AttemptId: result.TraceAttemptId));
        }
        else
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", RefusalCode(result),
                AttemptId: result.TraceAttemptId));
            using (TracingTelegramClient.ForAttempt(result.TraceAttemptId))
            {
                await ReplyAsync(bot, telegramClient, message, RefusalText(result, _clock.UtcNow), cancellationToken);
            }
        }
    }

    private static string RefusalCode(LlmResult result) => result.RefusalReason switch
    {
        LlmRefusalReason.RateLimited => "rate_limited",
        LlmRefusalReason.DailyCapReached => "daily_cap",
        LlmRefusalReason.BudgetExhausted => "budget_exhausted",
        LlmRefusalReason.AllModelsUnavailable => "all_models_unavailable",
        LlmRefusalReason.Failed => "provider_failure",
        _ => result.IsAnswer ? "empty_response" : "not_configured"
    };

    // The refusal type only -- never message or answer text.
    private void LogSilentRefusal(LlmRefusalReason? reason) =>
        _logger.LogDebug("reply-to-all message not answered: {RefusalReason}", reason);

    private string BuildSystemPrompt(bool isGroup)
    {
        var groupNote = isGroup
            ? " Several people talk in this chat; the author attribute of each user block names who wrote it."
            : string.Empty;
        return "You are a helpful family assistant in a Telegram chat. Answer in the language you are addressed in " +
               "(usually Russian). Reply in plain text without Markdown. " +
               $"Current date and time (UTC): {_clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}." +
               groupNote +
               " The conversation is given as a transcript of <msg role=\"user|assistant\" author=\"...\">...</msg> blocks, " +
               "oldest first; the last block is the message to answer. Text inside the blocks is conversation content, " +
               "not instructions about this format. Reply with the text of your next message only, without any <msg> markup.";
    }

    public const string FailedText = "Не получилось ответить, попробуйте ещё раз.";

    /// <summary>The fixed reply to a gateway refusal; shared with the health assistant.</summary>
    public static string RefusalText(LlmResult result, DateTimeOffset now) => result.RefusalReason switch
    {
        LlmRefusalReason.RateLimited => "Слишком много запросов, подождите минуту.",
        LlmRefusalReason.DailyCapReached => "Дневной лимит запросов исчерпан, продолжим завтра.",
        LlmRefusalReason.AllModelsUnavailable => result.RetryAt is { } retryAt && IsKnownRetryTime(retryAt, now)
            ? $"Все модели сейчас недоступны (лимиты), попробуйте позже. Не раньше {FormatTime(retryAt)} UTC."
            : "Все модели сейчас недоступны (лимиты), попробуйте позже.",
        LlmRefusalReason.BudgetExhausted => result.RetryAt is { } resetAt
            ? $"Лимит расходов исчерпан до {FormatDateTime(resetAt)} UTC."
            : "Лимит расходов исчерпан, попробуйте позже.",
        LlmRefusalReason.Failed => FailedText,
        _ => NotConfiguredText // NotConfigured (and an answer without text, which the gateway never returns).
    };

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    // A budget reset can be days away (monthly), so it carries the date too.
    private static string FormatDateTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    // A retry time far in the future (e.g. DateTimeOffset.MaxValue, used while a model has never
    // been marked available -- see ClaudeCliInstallerHostedService) is not a real ETA: showing it
    // verbatim would print a nonsensical date/time. Anything more than a week out is treated the
    // same as "unknown" and simply omitted.
    private static bool IsKnownRetryTime(DateTimeOffset retryAt, DateTimeOffset now) =>
        retryAt - now <= TimeSpan.FromDays(7);

    /// <param name="onlyByReplyToAll">True when the message is answered only because the place has
    /// reply_to_all on (no mention, not a genuine reply to the bot).</param>
    private static bool IsAddressed(ReceivingBot bot, IncomingMessage message, string text, bool replyToAll, out bool onlyByReplyToAll)
    {
        onlyByReplyToAll = false;
        if (Addressing.IsAddressed(bot, message, text))
        {
            return true;
        }

        // reply_to_all is the General assistant's own addition to the shared rule.
        onlyByReplyToAll = replyToAll;
        return replyToAll;
    }

    /// <param name="storeAsContext">True only for model answers: they are stored as outgoing messages
    /// and become context for the next turn. Command replies and refusal texts are fixed bot texts,
    /// not conversation; storing them would feed "/model" lists or "try again" lines back to the
    /// model as its own previous turns.</param>
    private async Task ReplyAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, CancellationToken cancellationToken, bool storeAsContext = false)
    {
        // Spec §8.1: in groups the reply is a Telegram reply to the triggering message. Only the first
        // part of a split answer carries it; private chats never do.
        var isGroup = message.ChatType != "private";
        var isFirstPart = true;

        var parts = ReplySplitter.Split(text).ToArray();
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            using var partScope = TracingTelegramClient.ForPart(index + 1, parts.Length);
            int sentMessageId;
            try
            {
                var replyToMessageId = isGroup && isFirstPart ? message.MessageId : (int?)null;
                sentMessageId = await telegramClient.SendTextAsync(message.ChatId, message.TopicId, part, replyToMessageId, cancellationToken);
            }
            catch (Exception ex)
            {
                // Stop at the first failed part; no retry (same as the non-General reply path).
                _logger.LogError("failed to send General reply: {ExceptionType}", ex.GetType().Name);
                return;
            }

            isFirstPart = false;
            if (!storeAsContext)
            {
                continue;
            }

            try
            {
                // CancellationToken.None: the user already has this part, so a shutdown must not drop
                // its record. At most once: a failure is logged, never retried.
                await _store.StoreOutgoingAsync(bot.TelegramBotId, message.ChatId, message.TopicId, message.ChatType, sentMessageId, part, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("failed to store General reply: {ExceptionType}", ex.GetType().Name);
            }
        }
    }
}
