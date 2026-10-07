using Assistant.Application.Common;
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

    public const string NonTextText = "Голосовые и фото пока не поддерживаются — напишите текстом.";

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
        ITraceSession? trace = null)
    {
        _trace = trace ?? NullTraceSession.Instance;
        _profiles = profiles;
        _replies = new HealthReplies(logger);
        var safety = new HealthSafety(profiles, events, safetyAlerts, clock, _replies, logger, _trace);
        var answers = new HealthAnswers(profiles, events, messages, gateway, config, rolePrompts, clock, logger, _trace);
        _confirmations = new HealthConfirmations(events, pendingRecords, clock, _replies, safety, logger, _trace);
        _commands = new HealthCommands(profiles, ownership, events, _replies, clock, buildInfo);
        _pipeline = new HealthMessagePipeline(
            profiles, events, gateway, rolePrompts, failureNotices, clock, _replies, safety, answers, _confirmations, logger, _trace);
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false)
    {
        if (bot.FamilyId is not { } familyId)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "not_configured"));
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

        // The profile (with its default rules) is created lazily by the bot's first text message.
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
