using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>The consultation answer: one `smart` call for an eligible question with the profile's
/// context, passed through the dose-advice filter and ending with the fixed footer. Never logs the
/// question, the note or the answer.</summary>
internal sealed class HealthAnswers
{
    private const string AnswerInstructionsFile = "prompt.md";

    private readonly IHealthProfileStore _profiles;
    private readonly IEventStore _events;
    private readonly IMessageStore _messages;
    private readonly ILlmGateway _gateway;
    private readonly LlmConfig? _config;
    private readonly IRolePrompts _rolePrompts;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly ITraceSession _trace;

    public HealthAnswers(
        IHealthProfileStore profiles, IEventStore events, IMessageStore messages, ILlmGateway gateway, LlmConfig? config,
        IRolePrompts rolePrompts, IClock clock, ILogger logger, ITraceSession? trace = null)
    {
        _profiles = profiles;
        _events = events;
        _messages = messages;
        _gateway = gateway;
        _config = config;
        _rolePrompts = rolePrompts;
        _clock = clock;
        _logger = logger;
        _trace = trace ?? NullTraceSession.Instance;
    }

    // One smart call for an eligible question: roles/health/prompt.md plus the runtime block (stage
    // week, context note, thresholds, readings of the last 24 hours) and the last few messages of this
    // chat/topic. The answer passes the dose-advice filter (dose advice replaces the whole answer with
    // the fixed refusal) and always ends with the footer. A refusal or failure gets a fixed text, never
    // silence, and is not stored. Logs the outcome only, never the question, the note or the answer.
    public async Task AnswerQuestionAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        long? messageDbId, CancellationToken cancellationToken)
    {
        var instructions = _rolePrompts.Find(BotRoles.Health, AnswerInstructionsFile);
        if (_config is null || instructions is null)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "not_configured"));
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
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "provider_failure"));
            _logger.LogError("health answer failed: {ExceptionType}", ex.GetType().Name);
            LogAnswer("failed", messageDbId);
            await SendAnswerAsync(bot, telegramClient, message, GeneralAssistant.FailedText, storeAsContext: false, cancellationToken);
            return;
        }

        if (!result.IsAnswer)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", result.RefusalReason switch
            {
                LlmRefusalReason.NotConfigured => "not_configured",
                LlmRefusalReason.RateLimited => "rate_limited",
                LlmRefusalReason.DailyCapReached => "daily_cap",
                LlmRefusalReason.BudgetExhausted => "budget_exhausted",
                LlmRefusalReason.AllModelsUnavailable => "all_models_unavailable",
                _ => "provider_failure"
            }, AttemptId: result.TraceAttemptId));
            LogAnswer($"refused:{result.RefusalReason}", messageDbId);
            using (TracingTelegramClient.ForAttempt(result.TraceAttemptId))
            {
                await SendAnswerAsync(
                    bot, telegramClient, message, GeneralAssistant.RefusalText(result, _clock.UtcNow), storeAsContext: false, cancellationToken);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "empty_response",
                AttemptId: result.TraceAttemptId));
            LogAnswer("empty", messageDbId);
            await SendAnswerAsync(bot, telegramClient, message, GeneralAssistant.FailedText, storeAsContext: false, cancellationToken);
            return;
        }

        // The deterministic filter is the enforcement (D7); the prompt rule alone is not trusted. The
        // model's own text of a replaced answer is never sent, logged or stored as conversation
        // context; opted-in private diagnostics mark it not sent.
        var blocked = DoseAdviceFilter.ContainsDoseAdvice(result.Text);
        var answer = blocked ? DoseAdviceFilter.RefusalText : result.Text.Trim();
        if (blocked)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("answer", "not_sent", "dose_advice_replaced",
                AttemptId: result.TraceAttemptId, Text: result.Text, Sent: false));
        }
        await TraceSafety.RecordAsync(_trace, new TraceEventData("answer", "generated", blocked ? "dose_advice_replaced" : "normal",
            AttemptId: result.TraceAttemptId, Text: $"{answer}\n\n{HealthAssistant.AnswerFooter}"));
        LogAnswer(blocked ? "dose_advice_replaced" : "answered", messageDbId);
        using (TracingTelegramClient.ForAttempt(result.TraceAttemptId))
        {
            await SendAnswerAsync(bot, telegramClient, message, $"{answer}\n\n{HealthAssistant.AnswerFooter}", storeAsContext: true, cancellationToken);
        }
    }

    private StageWeekResult CurrentWeek(HealthProfileInfo profile) =>
        StageWeek.Compute(ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone), profile.StageStartDate);

    // Like the General assistant's replies: split for Telegram; in groups the first part is a reply to
    // the question, private chats never quote. Only answers are stored as outgoing context.
    private async Task SendAnswerAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, bool storeAsContext,
        CancellationToken cancellationToken)
    {
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
}
