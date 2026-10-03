using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>"Ask before recording": values the model marked unsure wait in pending_records under one
/// "Записать …?" message with [Да]/[Нет]. The first valid tap decides (a conditional status update);
/// Да saves the events through the normal rules, ✍ and alerts (an alert already sent while asking is
/// not repeated); Нет stores nothing; a row older than 24 h expires on its next tap; an edit or a
/// free-text undo of the message closes its pending rows. Logs ids and counts only.</summary>
internal sealed class HealthConfirmations
{
    public const string YesLabel = "Да";
    public const string NoLabel = "Нет";
    public const string AlreadyDecidedText = "Уже решено.";
    public const string NotRecordedText = "Не записано.";
    public const string ExpiredText = "Время вышло — напишите значение ещё раз.";
    public const string SaveFailedText = "Не получилось записать — нажмите ещё раз.";

    /// <summary>The same window as /undo.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly IEventStore _events;
    private readonly IPendingRecordStore _pending;
    private readonly IClock _clock;
    private readonly HealthReplies _replies;
    private readonly HealthSafety _safety;
    private readonly ILogger _logger;

    public HealthConfirmations(
        IEventStore events, IPendingRecordStore pending, IClock clock, HealthReplies replies, HealthSafety safety, ILogger logger)
    {
        _events = events;
        _pending = pending;
        _clock = clock;
        _replies = replies;
        _safety = safety;
        _logger = logger;
    }

    /// <summary>"глюкоза 10.0 ммоль/л; давление 128/84".</summary>
    public static string ShortList(IEnumerable<NewHealthEvent> events) => string.Join("; ", events.Select(HealthEventText.Describe));

    // Stores the values and sends one reply to the message: "Записать …?" with [Да] [Нет]. A failed
    // store tells the family nothing was recorded and rethrows (like a failed save); a failed send
    // leaves the row without buttons (logged; a free-text undo can still close it).
    public async Task AskAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        long? messageDbId, IReadOnlyList<NewHealthEvent> events, IReadOnlyList<string> alertedRuleKeys, CancellationToken cancellationToken)
    {
        long id;
        try
        {
            id = await _pending.AddAsync(
                familyId, profile.Id,
                new NewPendingRecord(messageDbId, bot.TelegramBotId, message.ChatId, message.TopicId, message.MessageId, message.UserId, events, alertedRuleKeys),
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("saving a pending record failed: {ExceptionType}", ex.GetType().Name);
            await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
            throw;
        }

        var buttons = new[]
        {
            new InlineButton(YesLabel, PendingRecordCallback.Format(accept: true, id)),
            new InlineButton(NoLabel, PendingRecordCallback.Format(accept: false, id))
        };
        int promptMessageId;
        try
        {
            promptMessageId = await telegramClient.SendTextWithButtonsAsync(
                message.ChatId, message.TopicId, $"Записать {ShortList(events)}?", buttons, message.MessageId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("failed to send the record question: {ExceptionType}", ex.GetType().Name);
            return;
        }

        try
        {
            await _pending.SetPromptMessageAsync(familyId, id, promptMessageId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The buttons are already on the chat; a free-text undo can still close the row by its
            // original message. Not fatal, like the send failure above.
            _logger.LogError("failed to save the record question's prompt message: {ExceptionType}", ex.GetType().Name);
        }

        _logger.LogInformation("Pending record {PendingId} asked for message {MessageDbId}: {EventCount} values", id, messageDbId, events.Count);
    }

    // A tap on Да or Нет. UpdateHandler has already checked the member and the place.
    public async Task HandleCallbackAsync(
        ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, long familyId, HealthProfileInfo profile,
        CancellationToken cancellationToken)
    {
        if (!PendingRecordCallback.TryParse(callback.Data, out var accept, out var id))
        {
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, null, cancellationToken);
            return;
        }

        var pending = await _pending.FindAsync(familyId, id, cancellationToken);
        if (pending is null || pending.BotId != bot.TelegramBotId || pending.ChatId != callback.MessageChatId
            || pending.Status != PendingRecordStatuses.Pending)
        {
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, AlreadyDecidedText, cancellationToken);
            return;
        }

        if (_clock.UtcNow - pending.CreatedAt > Lifetime)
        {
            await ResolveWithTextAsync(
                telegramClient, callback, familyId, pending, PendingRecordStatuses.Expired, ExpiredText, cancellationToken);
            return;
        }

        if (!accept)
        {
            await ResolveWithTextAsync(
                telegramClient, callback, familyId, pending, PendingRecordStatuses.Declined, NotRecordedText, cancellationToken);
            return;
        }

        await AcceptAsync(telegramClient, callback, familyId, profile, pending, cancellationToken);
    }

    /// <summary>Closes every pending row of this Telegram message (the original or its button
    /// message) with status and edits its button message to text. Returns the rows closed.</summary>
    public async Task<IReadOnlyList<PendingRecordInfo>> CloseForMessageAsync(
        ITelegramClient telegramClient, long familyId, long botId, long chatId, int telegramMessageId, string status, string text,
        long? resolvedByUserId, CancellationToken cancellationToken)
    {
        var closed = new List<PendingRecordInfo>();
        foreach (var pending in await _pending.FindPendingByTelegramMessageAsync(familyId, botId, chatId, telegramMessageId, cancellationToken))
        {
            if (await CloseAsync(telegramClient, familyId, pending, status, text, resolvedByUserId, cancellationToken))
            {
                closed.Add(pending);
            }
        }

        return closed;
    }

    /// <summary>Closes one pending row with status and edits its button message to text. False when
    /// it was already decided.</summary>
    public async Task<bool> CloseAsync(
        ITelegramClient telegramClient, long familyId, PendingRecordInfo pending, string status, string text, long? resolvedByUserId,
        CancellationToken cancellationToken)
    {
        if (!await _pending.TryResolveAsync(familyId, pending.Id, status, resolvedByUserId, null, cancellationToken))
        {
            return false;
        }

        _logger.LogInformation("Pending record {PendingId} {Status}", pending.Id, status);
        if (pending.PromptMessageId is { } promptMessageId)
        {
            await EditPromptAsync(telegramClient, pending.ChatId, promptMessageId, text, cancellationToken);
        }

        return true;
    }

    /// <summary>Free-text undo without a usable reply: declines the sender's newest pending row in
    /// this place (created at or after createdAfter) when it belongs to a newer message than their
    /// newest recorded one (newerThanSourceMessageId, messages.id; null = none). Returns that row, or
    /// null when the recorded message is the newer one or nothing was declined.</summary>
    public async Task<PendingRecordInfo?> DeclineLatestOfUserAsync(
        ITelegramClient telegramClient, long familyId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter,
        long? newerThanSourceMessageId, CancellationToken cancellationToken)
    {
        var pending = await _pending.FindLatestPendingOfUserAsync(familyId, botId, chatId, topicId, userId, createdAfter, cancellationToken);
        if (pending?.SourceMessageId is not { } source || (newerThanSourceMessageId is { } recorded && source <= recorded))
        {
            return null;
        }

        return await CloseAsync(telegramClient, familyId, pending, PendingRecordStatuses.Declined, NotRecordedText, userId, cancellationToken)
            ? pending
            : null;
    }

    private async Task ResolveWithTextAsync(
        ITelegramClient telegramClient, CallbackQueryInfo callback, long familyId, PendingRecordInfo pending, string status, string text,
        CancellationToken cancellationToken)
    {
        if (!await _pending.TryResolveAsync(familyId, pending.Id, status, callback.FromUserId, null, cancellationToken))
        {
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, AlreadyDecidedText, cancellationToken);
            return;
        }

        _logger.LogInformation("Pending record {PendingId} {Status}", pending.Id, status);
        await EditPromptAsync(telegramClient, callback.MessageChatId, callback.MessageId, text, cancellationToken);
        await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, null, cancellationToken);
    }

    // Да: the rules run again with the current rules (flags), then the status change and the save
    // commit together; only the tap that changed the row saves. The events keep the original sender
    // and message, so /undo and /del treat them like any other record of that message.
    private async Task AcceptAsync(
        ITelegramClient telegramClient, CallbackQueryInfo callback, long familyId, HealthProfileInfo profile, PendingRecordInfo pending,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthEventInfo> saved = Array.Empty<HealthEventInfo>();
        IReadOnlyList<SafetyEvaluation> evaluations;
        bool accepted;
        try
        {
            // null, not pending.SourceMessageId: the pending events are not stored yet (unlike an edit's
            // own earlier events), so there is nothing of this message to exclude from the combo context
            // — excluding by messageDbId here would instead drop a sibling event of this same message
            // that was already recorded (saved as "record" intent at extraction time).
            evaluations = await _safety.EvaluateAsync(familyId, profile, pending.Events, null, cancellationToken);
            var flagged = pending.Events.Select((e, i) => e with { Flags = evaluations[i].Flags }).ToList();
            var source = new HealthEventSource(pending.SourceMessageId, pending.BotId, pending.ChatId, pending.TopicId, pending.RequestedByUserId);
            accepted = await _pending.TryResolveAsync(
                familyId, pending.Id, PendingRecordStatuses.Accepted, callback.FromUserId,
                async token => saved = await _events.AddAsync(familyId, profile.Id, source, flagged, token),
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Nothing changed: the row is still pending, so the next tap tries again.
            _logger.LogError("saving a confirmed record failed: {ExceptionType}", ex.GetType().Name);
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, SaveFailedText, cancellationToken);
            return;
        }

        if (!accepted)
        {
            await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, AlreadyDecidedText, cancellationToken);
            return;
        }

        _logger.LogInformation("Pending record {PendingId} accepted: {EventCount} events", pending.Id, saved.Count);
        await _replies.MarkRecordedAsync(telegramClient, pending.ChatId, pending.TelegramMessageId, cancellationToken);
        await _safety.SendAlertsAsync(
            telegramClient, pending.ChatId, pending.TopicId, pending.TelegramMessageId, familyId, profile, saved, evaluations,
            pending.SourceMessageId, cancellationToken, alreadySent: pending.AlertedRuleKeys);
        await EditPromptAsync(telegramClient, callback.MessageChatId, callback.MessageId, $"Записано: {ShortList(pending.Events)}.", cancellationToken);
        await _replies.AnswerCallbackAsync(telegramClient, callback.CallbackQueryId, null, cancellationToken);
    }

    private async Task EditPromptAsync(ITelegramClient telegramClient, long chatId, int messageId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await telegramClient.EditMessageTextAsync(chatId, messageId, text, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("failed to edit the record question: {ExceptionType}", ex.GetType().Name);
        }
    }
}
