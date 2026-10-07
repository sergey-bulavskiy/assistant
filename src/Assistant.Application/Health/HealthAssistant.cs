using Assistant.Application.Common;
using Assistant.Application.Health.Documents;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>The `health` role bot: a health tracking assistant for one household member (one profile
/// per bot, created lazily with the default safety rules). The entry point and dispatcher: commands go
/// to <see cref="HealthCommands"/>, every other text message (new or edited) to
/// <see cref="HealthMessagePipeline"/>, which uses <see cref="HealthAnswers"/> for eligible replies.
/// Never logs message text, model answers or values.</summary>
public class HealthAssistant : IHealthAssistant
{
    public const string OwnerOnlyText = "Только владелец семьи может менять профиль.";

    public const string NonTextText = "Голосовые и фото пока не поддерживаются — напишите текстом или отправьте текстовый PDF, UTF-8 .txt или .md до 20 МБ.";

    /// <summary>✍ (U+270D, no variation selector: the form Telegram allows for bots).</summary>
    public const string RecordedReaction = "✍";

    /// <summary>👍, tried once when the chat refuses ✍.</summary>
    public const string FallbackReaction = "\U0001F44D";

    /// <summary>Ends every complete answer that came from the model.</summary>
    public const string AnswerFooter = "Не заменяю врача.";

    private readonly IHealthProfileStore _profiles;
    private readonly HealthReplies _replies;
    private readonly HealthCommands _commands;
    private readonly HealthMessagePipeline _pipeline;
    private readonly HealthConfirmations _confirmations;
    private readonly ITraceSession _trace;
    private readonly IHealthDocumentStore? _documents;
    private readonly HealthDocumentProcessor? _documentProcessor;
    private readonly ILogger _logger;

    public HealthAssistant(
        IHealthProfileStore profiles,
        IFamilyOwnership ownership,
        IEventStore events,
        ISafetyAlertStore safetyAlerts,
        IPendingRecordStore pendingRecords,
        IMessageStore messages,
        ILlmGateway gateway,
        LlmConfig? config,
        IRolePrompts rolePrompts,
        FailureNoticeThrottle failureNotices,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<HealthAssistant> logger,
        ITraceSession? trace = null, IHealthDocumentStore? documents = null, HealthDocumentProcessor? documentProcessor = null)
    {
        _trace = trace ?? NullTraceSession.Instance;
        _documents = documents;
        _documentProcessor = documentProcessor;
        _logger = logger;
        _profiles = profiles;
        _replies = new HealthReplies(logger, documents);
        var safety = new HealthSafety(profiles, events, safetyAlerts, clock, _replies, logger, _trace);
        var answers = new HealthAnswers(profiles, events, messages, gateway, config, rolePrompts, clock, logger, _trace, documents);
        _confirmations = new HealthConfirmations(events, pendingRecords, clock, _replies, safety, logger, _trace);
        _commands = new HealthCommands(profiles, ownership, events, _replies, clock, buildInfo, documents);
        _pipeline = new HealthMessagePipeline(
            profiles, events, gateway, rolePrompts, failureNotices, clock, _replies, safety, answers, _confirmations, logger, _trace);
    }

    public async Task<HealthDocumentAdmissionInfo?> AdmitDocumentAsync(
        ReceivingBot bot, IncomingMessage message, long updateId, CancellationToken token)
    {
        if (_documents is null || bot.FamilyId is not { } familyId || !HealthDocumentCandidate.IsValid(message)) return null;
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, token);
        return await _documents.AdmitAsync(HealthDocumentScope.From(bot, profile.Id), message, updateId, token);
    }

    public async Task ResumeDocumentsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken token)
    {
        if (_documentProcessor is null || bot.FamilyId is not { } familyId) return;
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, token);
        await _documentProcessor.ResumeAsync(HealthDocumentScope.From(bot, profile.Id), client, token);
    }

    private async Task HandleDocumentAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        StoreResult result, long familyId, CancellationToken token, bool replyToAll)
    {
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, token);
        Exception? failure = null;
        try
        {
            var admission = await _documents!.FindAsync(HealthDocumentScope.From(bot, profile.Id), message.ChatId, message.TopicId, message.MessageId, token);
            var bound = admission is null ? null : await _documents.BindAsync(admission.Scope, admission.Id, result.MessageDbId, token);
            if (bound?.SourceMessageId is not null)
                await _documentProcessor!.ProcessAsync(bound, client, token);
            else if (result.Outcome == StoreOutcome.Stored)
                await _replies.ReplyAsync(client, message, HealthDocumentProcessor.CannotRead, token, quote: true);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("Health document processing failed: {ExceptionType}", ex.GetType().Name);
            failure = new InvalidOperationException("Health document processing failed.");
            if (result.Outcome == StoreOutcome.Stored)
                await _replies.ReplyAsync(client, message, HealthDocumentProcessor.CannotSave, token, quote: true);
        }
        // Only the original caption may be interpreted. It is data, never a slash command or undo.
        if (result.Outcome == StoreOutcome.Stored && !string.IsNullOrWhiteSpace(message.Text))
            await _pipeline.HandleNewAsync(bot, client, message, message.Text, familyId, profile, result, token, replyToAll, allowUndo: false);
        if (failure is not null) throw failure;
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false)
    {
        if (bot.FamilyId is not { } familyId)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "not_configured"));
            return;
        }

        if (!message.IsEdit && message.Kind == MessageKind.Document && _documents is not null && _documentProcessor is not null)
        {
            await HandleDocumentAsync(bot, telegramClient, message, storeResult, familyId, cancellationToken, replyToAll);
            return;
        }

        // An edit is read again (Updated; or Stored when the bot never saw the original). Redeliveries
        // (AlreadyProcessed, Duplicate, OffsetOnly) never reply twice.
        if (message.IsEdit)
        {
            if (storeResult.Outcome is StoreOutcome.Stored or StoreOutcome.Updated)
            {
                await _pipeline.HandleEditAsync(bot, telegramClient, message, familyId, storeResult, cancellationToken);
            }

            else
            {
                await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "already_processed"));
            }

            return;
        }

        if (storeResult.Outcome != StoreOutcome.Stored)
        {
            return;
        }

        if (message.Kind == MessageKind.Service)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "service_or_non_text"));
            return;
        }

        if (message.Kind != MessageKind.Text || message.Text is not { } text)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "service_or_non_text"));
            // Voice and photos are not supported yet: say so in private chats, stay silent in groups.
            if (message.ChatType == "private")
            {
                await _replies.ReplyAsync(telegramClient, message, NonTextText, cancellationToken);
            }

            return;
        }

        // The profile (with its default rules) is created lazily by the bot's first text or document.
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);

        var command = CommandParser.Parse(text, bot.Username);
        if (command is null)
        {
            // /cmd@otherbot (or any other slash text) is silent; everything else may hold readings.
            if (!text.StartsWith('/'))
            {
                await _pipeline.HandleNewAsync(bot, telegramClient, message, text, familyId, profile, storeResult, cancellationToken, replyToAll);
            }
            else
            {
                await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "other_bot_command"));
            }

            return;
        }

        await _commands.HandleAsync(
            command, CommandParser.ParseArgs(text), bot, telegramClient, message, familyId, profile, cancellationToken);
    }

    public async Task HandleCallbackAsync(
        ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, CancellationToken cancellationToken)
    {
        if (bot.FamilyId is not { } familyId)
        {
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, null, cancellationToken);
            return;
        }

        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);
        await _confirmations.HandleCallbackAsync(bot, telegramClient, callback, familyId, profile, cancellationToken);
    }
}
