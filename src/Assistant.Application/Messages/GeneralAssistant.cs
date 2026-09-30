using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Application.Common;
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
        "в группах — только если обратиться по имени или ответить на моё сообщение. " +
        "/new — начать разговор заново. /model — выбрать модель. /version — версия.";

    private const string NewConversationText = "Начинаем новый разговор.";

    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    private readonly IMessageStore _store;
    private readonly ILlmGateway _gateway;
    private readonly IChatSettingsStore _chatSettings;
    private readonly LlmConfig? _config;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<GeneralAssistant> _logger;

    /// <param name="config">Null when LLM is off or its config is invalid: every question then gets
    /// <see cref="NotConfiguredText"/> without touching settings, context or the gateway.</param>
    public GeneralAssistant(
        IMessageStore store,
        ILlmGateway gateway,
        IChatSettingsStore chatSettings,
        LlmConfig? config,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<GeneralAssistant> logger)
    {
        _store = store;
        _gateway = gateway;
        _chatSettings = chatSettings;
        _config = config;
        _clock = clock;
        _buildInfo = buildInfo;
        _logger = logger;
    }

    public async Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken)
    {
        if (storeResult.Outcome is StoreOutcome.AlreadyProcessed or StoreOutcome.Duplicate or StoreOutcome.OffsetOnly)
        {
            return;
        }

        if (message.Kind != MessageKind.Text || message.IsEdit || message.Text is not { } text || bot.FamilyId is not { } familyId)
        {
            // Service, non-text (photos/voice are M7) and edited messages are stored, never answered.
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
            return;
        }

        if (!IsAddressed(bot, message, text))
        {
            return;
        }

        await AnswerAsync(bot, telegramClient, message, text, familyId, storeResult, cancellationToken);
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

            case "version":
                await ReplyAsync(bot, telegramClient, message, VersionText.Format(_buildInfo, _clock.UtcNow), cancellationToken);
                return;

            default:
                return; // Unknown command (or /start in a group): silent.
        }
    }

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
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, current.PreferredModel), cancellationToken);
            return;
        }

        if (string.Equals(arg, "auto", StringComparison.OrdinalIgnoreCase))
        {
            await _chatSettings.SetPreferredModelAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, null, cancellationToken);
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, null), cancellationToken);
            return;
        }

        // Store the catalog's own spelling of the name, so "/model HAIKU" and "/model haiku" agree.
        // Nit: match already comes from models (_gateway.DescribeModels()), so it is by construction
        // a name the gateway knows -- a further _gateway.IsKnownModel(match.Name) check was redundant.
        var match = models.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await ReplyAsync(bot, telegramClient, message, DescribeModels(models, current.PreferredModel), cancellationToken);
            return;
        }

        await _chatSettings.SetPreferredModelAsync(familyId, bot.TelegramBotId, message.ChatId, message.TopicId, match.Name, cancellationToken);
        await ReplyAsync(bot, telegramClient, message, DescribeModels(models, match.Name), cancellationToken);
    }

    private static string DescribeModels(IReadOnlyList<ModelStatus> models, string? preferred)
    {
        var lines = models.Select(m =>
        {
            var marker = string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase) ? " (текущая)" : string.Empty;
            var status = m.IsAvailable
                ? "доступна"
                : m.RetryAt is { } retryAt
                    ? $"недоступна до {FormatTime(retryAt)} UTC"
                    : "недоступна";
            return $"- {m.Name}: {status}{marker}";
        });

        var current = preferred ?? "auto";
        return $"Модели:\n{string.Join("\n", lines)}\n\nНастройка этого чата: {current}. " +
               "/model <имя> — выбрать модель, /model auto — по умолчанию.";
    }

    private async Task AnswerAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, StoreResult storeResult, CancellationToken cancellationToken)
    {
        if (_config is null)
        {
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
        var request = new LlmRequest(familyId, bot.TelegramBotId, LlmConfig.SmartTier, chatSetting.PreferredModel, BuildSystemPrompt(isGroup), llmMessages);

        LlmResult result;
        using (var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var typingTask = RunTypingLoopAsync(telegramClient, message.ChatId, message.TopicId, typingCts.Token);
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
            await ReplyAsync(bot, telegramClient, message, result.Text, cancellationToken, storeAsContext: true);
        }
        else
        {
            await ReplyAsync(bot, telegramClient, message, RefusalText(result), cancellationToken);
        }
    }

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

    private const string FailedText = "Не получилось ответить, попробуйте ещё раз.";

    private static string RefusalText(LlmResult result) => result.RefusalReason switch
    {
        LlmRefusalReason.RateLimited => "Слишком много запросов, подождите минуту.",
        LlmRefusalReason.DailyCapReached => "Дневной лимит запросов исчерпан, продолжим завтра.",
        LlmRefusalReason.AllModelsUnavailable => result.RetryAt is { } retryAt
            ? $"Все модели сейчас недоступны (лимиты), попробуйте позже. Не раньше {FormatTime(retryAt)} UTC."
            : "Все модели сейчас недоступны (лимиты), попробуйте позже.",
        LlmRefusalReason.Failed => FailedText,
        _ => NotConfiguredText // NotConfigured (and an answer without text, which the gateway never returns).
    };

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool IsAddressed(ReceivingBot bot, IncomingMessage message, string text)
    {
        if (message.ChatType == "private")
        {
            return true;
        }

        var mentioned = MentionsBot(text, bot.Username);

        // Ordinary forum-topic messages carry reply_to_message == the topic root; that is not a reply
        // to the bot (spec 2.2), even when the bot happens to have sent the root.
        var isReplyToTopicRoot = message.TopicId is { } topicId && message.ReplyToMessageId == topicId;
        var isGenuineReplyToBot = message.ReplyToUserId == bot.TelegramBotId && !isReplyToTopicRoot;

        return mentioned || isGenuineReplyToBot;
    }

    // Spec §8.1: @username followed by end of text or a non-word character (@bot does not match
    // @bot2). Nit: also requires no word character or '@' immediately before the '@' (a negative
    // lookbehind), so an email-like "me@test_bot" is never mistaken for an actual mention.
    private static bool MentionsBot(string text, string botUsername) =>
        Regex.IsMatch(text, $@"(?<![\w@])@{Regex.Escape(botUsername)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static async Task RunTypingLoopAsync(ITelegramClient telegramClient, long chatId, int? topicId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await telegramClient.SendChatActionAsync(chatId, topicId, "typing", cancellationToken);
            }
            catch (Exception)
            {
                // Best-effort: a failed typing indicator never affects the reply (and is not logged,
                // it would only add noise every 4 s while Telegram is unreachable).
            }

            try
            {
                await Task.Delay(TypingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
