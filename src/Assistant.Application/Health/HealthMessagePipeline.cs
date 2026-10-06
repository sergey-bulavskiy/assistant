using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>A text message (new or edited) of the health bot: one `fast` extraction call (JSON
/// answer, strict parser, code-side validation), safety rules on every new event (flags saved with
/// it, one fixed alert per dangerous event), ✍ on recorded messages, one clarification when something
/// cannot be recorded, the quick scan when extraction fails or missed a reading, and the answer or
/// the hint for an addressed message. Never logs message text, model answers or values.</summary>
internal sealed class HealthMessagePipeline
{
    private const string ExtractInstructionsFile = "extract.md";

    public const string AnonymousUndoText = "Не могу определить автора — ответьте на сообщение командой /del.";

    /// <summary>Edits of messages sent longer ago than this are ignored (their records stay).</summary>
    private static readonly TimeSpan EditWindow = TimeSpan.FromHours(24);

    private readonly IHealthProfileStore _profiles;
    private readonly IEventStore _events;
    private readonly ILlmGateway _gateway;
    private readonly IRolePrompts _rolePrompts;
    private readonly FailureNoticeThrottle _failureNotices;
    private readonly AddressedHintThrottle _hints;
    private readonly IClock _clock;
    private readonly HealthReplies _replies;
    private readonly HealthSafety _safety;
    private readonly HealthAnswers _answers;
    private readonly HealthConfirmations _confirmations;
    private readonly ILogger _logger;
    private readonly ITraceSession _trace;

    public HealthMessagePipeline(
        IHealthProfileStore profiles, IEventStore events, ILlmGateway gateway, IRolePrompts rolePrompts, FailureNoticeThrottle failureNotices,
        AddressedHintThrottle hints, IClock clock, HealthReplies replies, HealthSafety safety, HealthAnswers answers,
        HealthConfirmations confirmations, ILogger logger, ITraceSession? trace = null)
    {
        _profiles = profiles;
        _events = events;
        _gateway = gateway;
        _rolePrompts = rolePrompts;
        _failureNotices = failureNotices;
        _hints = hints;
        _clock = clock;
        _replies = replies;
        _safety = safety;
        _answers = answers;
        _confirmations = confirmations;
        _logger = logger;
        _trace = trace ?? NullTraceSession.Instance;
    }

    // An edited text message is read again and its records follow the new text. Ignored: non-text
    // edits, edits into a command (commands run only when sent) and edits of messages sent more than
    // EditWindow ago; their records stay. Everything else (rules, alerts, clarification, quick scan,
    // failure notice) works as for a new message.
    public async Task HandleEditAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, StoreResult storeResult,
        CancellationToken cancellationToken)
    {
        if (message.Kind != MessageKind.Text || message.Text is not { } text || text.StartsWith('/') || storeResult.MessageDbId is null)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "edit_suppressed"));
            return;
        }

        if (_clock.UtcNow - message.SentAt > EditWindow)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "edit_suppressed"));
            _logger.LogInformation("Edit of message {MessageDbId} ignored: sent too long ago", storeResult.MessageDbId);
            return;
        }

        // Values of this message still waiting for Да/Нет are dropped: the new text is read again below.
        // Rules whose fixed alert already went out while asking must not alert again for the same
        // value once the edit re-records it (SendAlertsAsync's alreadySent, the same mechanism Да uses).
        var closed = await _confirmations.CloseForMessageAsync(
            telegramClient, familyId, bot.TelegramBotId, message.ChatId, message.MessageId, PendingRecordStatuses.Expired,
            HealthConfirmations.ExpiredText, resolvedByUserId: null, cancellationToken);
        var alreadyAlertedRuleKeys = closed.SelectMany(p => p.AlertedRuleKeys).ToHashSet();

        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);
        await ExtractAsync(
            bot, telegramClient, message, text, familyId, profile, storeResult, cancellationToken, isEdit: true,
            alreadyAlertedRuleKeys: alreadyAlertedRuleKeys);
    }

    /// <summary>A new text message that is not a command.</summary>
    public Task HandleNewAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll) =>
        ExtractAsync(bot, telegramClient, message, text, familyId, profile, storeResult, cancellationToken, replyToAll: replyToAll);

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
            await _replies.MarkRecordedAsync(telegramClient, message.ChatId, message.MessageId, cancellationToken);
        }
        else if (replaced.Events.Count == 0 && replaced.HadEvents)
        {
            await _replies.ClearReactionsAsync(telegramClient, new[] { new MessageRef(message.ChatId, message.MessageId) }, cancellationToken);
        }
    }

    private async Task ExtractAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, string text, long familyId, HealthProfileInfo profile,
        StoreResult storeResult, CancellationToken cancellationToken, bool isEdit = false,
        IReadOnlyCollection<string>? alreadyAlertedRuleKeys = null, bool replyToAll = false)
    {
        var messageDbId = storeResult.MessageDbId;
        if (!ExtractionPrompt.ShouldExtract(text))
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "no_extraction_candidate"));
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
            await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "failed", "not_configured"));
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
            await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "failed", "extraction_failed"));
            _logger.LogError("health extraction call failed: {ExceptionType}", ex.GetType().Name);
            LogOutcome("failed", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
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
            await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "failed", "extraction_failed",
                AttemptId: result.TraceAttemptId));
            LogOutcome($"refused:{result.RefusalReason}", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
            return;
        }

        var output = ExtractionParser.Parse(result.Text);
        if (output is null)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "failed", "invalid_extraction",
                AttemptId: result.TraceAttemptId));
            LogOutcome("invalid_output", messageDbId, 0);
            await HandleExtractionFailureAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, isEdit, cancellationToken);
            return;
        }

        // Every valid value goes through the safety rules; what is saved depends on the model's intent.
        // An edit corrects a record, so there an unsure value counts as a record (edits never ask).
        var valid = new List<NewHealthEvent>();
        var intents = new List<string>();
        var problems = output.Unclear.Where(problem => ExtractionReplies.ShouldClarify(problem, text)).ToList();
        foreach (var extracted in output.Events)
        {
            var validation = HealthEventValidator.Validate(extracted, message.SentAt, profile.TimeZone);
            if (validation.Event is { } recordable)
            {
                valid.Add(recordable);
                var intent = extracted.Intent ?? ExtractionIntents.Record;
                intents.Add(isEdit && intent == ExtractionIntents.Unsure ? ExtractionIntents.Record : intent);
            }
            else if (validation.Problem is { } problem)
            {
                problems.Add(problem);
            }
        }

        await TraceSafety.RecordAsync(_trace, new TraceEventData(
            "extraction", "ok", "normal",
            AttemptId: result.TraceAttemptId, EventCount: valid.Count, ProblemCount: problems.Count));

        // A free-text undo ("удали это", "не записывай"): the model decides the message asks for it; code
        // acts only on a new message that recorded nothing and is a reply or addressed to the bot, and
        // limits it to one message in the /undo scope. Otherwise the message is handled as usual.
        if (!isEdit && output.Undo && valid.Count == 0 && (IsReply(message) || Addressing.IsAddressed(bot, message, text)))
        {
            await UndoByTextAsync(bot, telegramClient, message, familyId, profile, cancellationToken);
            // A dangerous reading in one of the quick-scan formats still gets its fixed alert even when
            // the message asked to undo something: the undo never swallows a safety alert.
            await TrySendQuickScanReplyAsync(
                telegramClient, message, text, familyId, profile, messageDbId, new HashSet<string>(), clarify: false, cancellationToken);
            LogOutcome("undo", messageDbId, 0);
            await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "completed", "undo",
                AttemptId: result.TraceAttemptId, EventCount: 0, ProblemCount: problems.Count));
            return;
        }

        var recordIndexes = Enumerable.Range(0, valid.Count).Where(i => intents[i] == ExtractionIntents.Record).ToList();
        IReadOnlyList<SafetyEvaluation> evaluations = Array.Empty<SafetyEvaluation>();
        var alertDecided = false;
        // An edit replaces the message's records even when the new text has none left.
        if (recordIndexes.Count > 0 || isEdit)
        {
            var source = new HealthEventSource(messageDbId, bot.TelegramBotId, message.ChatId, message.TopicId, message.UserId);
            var evaluated = false;
            IReadOnlyList<HealthEventInfo> saved;
            ReplacedEvents? replaced = null;
            try
            {
                // Safety rules (deterministic code, never the model) run on every valid value before
                // anything is saved; the flags of the recorded ones are saved with them. On an edit they
                // run on every event of the new text: an unchanged event keeps its id, so its existing
                // alert claim stops a second alert.
                if (valid.Count > 0)
                {
                    evaluations = await _safety.EvaluateAsync(familyId, profile, valid, messageDbId, cancellationToken);
                    evaluated = true;
                }

                var flagged = recordIndexes.Select(i => valid[i] with { Flags = evaluations[i].Flags }).ToList();
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
                    await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "completed", "fixed_alert"));
                    await _replies.ReplyAsync(
                        telegramClient, message, SafetyAlertText.FormatNotRecorded(unsaved, profile.EmergencyPhone), cancellationToken, quote: true);
                    _logger.LogWarning(
                        "Safety alert {RuleKey} ({Level}) for unsaved message {MessageDbId}", unsaved.RuleKey, unsaved.Level, messageDbId);
                }
                else if (!evaluated && QuickReadingScanner.Scan(text).Count > 0)
                {
                    await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
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
                await _replies.MarkRecordedAsync(telegramClient, message.ChatId, message.MessageId, cancellationToken);
            }
            else
            {
                await UpdateEditReactionAsync(telegramClient, message, replaced, messageDbId, cancellationToken);
            }

            alertDecided = await _safety.SendAlertsAsync(
                telegramClient, message.ChatId, message.TopicId, message.MessageId, familyId, profile, saved,
                recordIndexes.Select(i => evaluations[i]).ToList(), messageDbId, cancellationToken, alreadySent: alreadyAlertedRuleKeys);
        }
        else if (valid.Count > 0)
        {
            // Nothing to save, but the values are still checked by the safety rules.
            try
            {
                evaluations = await _safety.EvaluateAsync(familyId, profile, valid, messageDbId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Like a failed save: say so (not throttled), then rethrow.
                _logger.LogError("safety check failed: {ExceptionType}", ex.GetType().Name);
                LogOutcome("rules_failed", messageDbId, 0);
                await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
                throw;
            }
        }

        // Values that are not saved (question_only, and unsure ones waiting for Да/Нет) get their fixed
        // alert at once: a dangerous value never waits for an answer. The normal alert text (an unsure
        // value may still be recorded); no claim, there is no event row. The rules sent for unsure values
        // are kept with the pending row, so Да does not send them again.
        var alertedRuleKeys = new List<string>();
        for (var i = 0; i < valid.Count; i++)
        {
            if (intents[i] == ExtractionIntents.Record || evaluations[i].Alert is not { } decision)
            {
                continue;
            }

            if (await SendUnsavedAlertAsync(telegramClient, message, profile, decision, messageDbId, cancellationToken)
                && intents[i] == ExtractionIntents.Unsure)
            {
                alertedRuleKeys.Add(decision.RuleKey);
            }
        }

        if (problems.Count > 0)
        {
            // One clarification per message, about the first problem; recorded values stay recorded and
            // unsure values are not asked about (the clarification wins).
            await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.Clarification(problems[0], text), cancellationToken, quote: true);
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

        var toAsk = problems.Count == 0
            ? Enumerable.Range(0, valid.Count).Where(i => intents[i] == ExtractionIntents.Unsure).Select(i => valid[i]).ToList()
            : new List<NewHealthEvent>();

        LogOutcome(
            recordIndexes.Count > 0 ? "events" : toAsk.Count > 0 ? "pending" : problems.Count > 0 ? "clarify" : "no_events",
            messageDbId, recordIndexes.Count);
        await TraceSafety.RecordAsync(_trace, new TraceEventData("extraction", "completed",
            recordIndexes.Count > 0 ? "events_recorded" : toAsk.Count > 0 ? "pending_record" : problems.Count > 0 ? "clarification" : "no_events",
            AttemptId: result.TraceAttemptId, EventCount: recordIndexes.Count, ProblemCount: problems.Count));

        // An answer for a new message that the model marked as a question, even
        // when readings were recorded from the same message (the answer's context then already holds
        // them). Never after an edit, a clarification, a quick-scan reply or an alert for a recorded
        // value: that fixed reply is the answer. An alert for a value that was not saved does not stop
        // the answer. The approved place setting also permits passive questions.
        if (!isEdit && output.IsQuestion && problems.Count == 0 && !quickScanSent && !alertDecided
            && (Addressing.IsAddressed(bot, message, text) || replyToAll))
        {
            await _answers.AnswerQuestionAsync(bot, telegramClient, message, text, familyId, profile, messageDbId, cancellationToken);
        }
        else if (output.IsQuestion)
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped",
                alertDecided ? "alert_precedence" : problems.Count > 0 || quickScanSent || isEdit ? "answer_suppressed" : "not_addressed",
                AttemptId: result.TraceAttemptId));
        }
        else if (!isEdit && !output.IsQuestion && valid.Count == 0 && problems.Count == 0 && !quickScanSent
            && Addressing.IsAddressed(bot, message, text)
            && _hints.TryAcquire(bot.TelegramBotId, message.ChatId, message.TopicId, _clock.UtcNow))
        {
            // Extraction succeeded and the addressed message produced nothing at all (a greeting,
            // chatter): one short fixed hint, throttled per place so chatter does not trigger it every time.
            await _replies.ReplyAsync(telegramClient, message, HealthAssistant.AddressedHintText, cancellationToken, quote: true);
        }
        else
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", "question_false",
                AttemptId: result.TraceAttemptId));
        }

        // The Да/Нет question comes last, after the alerts and the answer (also in unaddressed messages).
        if (toAsk.Count > 0)
        {
            await _confirmations.AskAsync(bot, telegramClient, message, familyId, profile, messageDbId, toAsk, alertedRuleKeys, cancellationToken);
        }
    }

    // Target = one message: the replied-to message when it has records or an open question (its records
    // are soft-deleted with reason undo, its question declined), else the sender's newest recorded
    // message in this chat/topic within the /undo window, or their newest open question when that
    // belongs to a newer message. Raw messages and sent alerts stay. No confirmation (like /undo).
    private async Task UndoByTextAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        CancellationToken cancellationToken)
    {
        var deleted = DeletedEvents.None;
        IReadOnlyList<PendingRecordInfo> declined = Array.Empty<PendingRecordInfo>();
        if (IsReply(message) && message.ReplyToMessageId is { } repliedTo)
        {
            // Limited to the /undo window: a reply to a record older than that is not something a
            // free-text undo may remove (unlike /del, which has no age limit).
            deleted = await _events.DeleteBySourceTelegramMessageAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, repliedTo, EventDeleteReasons.Undo, cancellationToken,
                createdAfter: _clock.UtcNow - HealthCommands.UndoWindow);
            declined = await _confirmations.CloseForMessageAsync(
                telegramClient, familyId, bot.TelegramBotId, message.ChatId, repliedTo, PendingRecordStatuses.Declined,
                HealthConfirmations.NotRecordedText, message.UserId, cancellationToken);
        }

        if (deleted.Events.Count == 0 && declined.Count == 0)
        {
            if (message.UserId is not { } userId)
            {
                await _replies.ReplyAsync(telegramClient, message, AnonymousUndoText, cancellationToken);
                return;
            }

            var since = _clock.UtcNow - HealthCommands.UndoWindow;
            var latestRecorded = await _events.FindLatestSourceMessageOfUserAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, message.TopicId, userId, since, cancellationToken);
            var question = await _confirmations.DeclineLatestOfUserAsync(
                telegramClient, familyId, bot.TelegramBotId, message.ChatId, message.TopicId, userId, since, latestRecorded, cancellationToken);
            if (question is not null)
            {
                declined = new[] { question };
            }
            else if (latestRecorded is not null)
            {
                deleted = await _events.DeleteLatestOfUserAsync(
                    familyId, profile.Id, bot.TelegramBotId, message.ChatId, message.TopicId, userId, since, EventDeleteReasons.Undo,
                    cancellationToken);
            }
        }

        if (deleted.Events.Count > 0)
        {
            await _replies.ClearReactionsAsync(telegramClient, deleted.MessagesWithoutEvents, cancellationToken);
            await _replies.ReplyAsync(telegramClient, message, HealthReplies.DeletedText(deleted), cancellationToken);
        }
        else if (declined.Count > 0)
        {
            await _replies.ReplyAsync(telegramClient, message, HealthConfirmations.NotRecordedText, cancellationToken);
        }
        else
        {
            await _replies.ReplyAsync(telegramClient, message, HealthCommands.NothingToUndoText, cancellationToken);
        }
    }

    // A reply to the forum topic root is how every topic message looks; it is not a reply.
    private static bool IsReply(IncomingMessage message) =>
        message.ReplyToMessageId is { } repliedTo && !(message.TopicId is { } topicId && repliedTo == topicId);

    // A fixed alert for a value that is not saved, sent at once as a reply (retried once). True when
    // it was delivered. Logs rule key and level only.
    private async Task<bool> SendUnsavedAlertAsync(
        ITelegramClient telegramClient, IncomingMessage message, HealthProfileInfo profile, SafetyDecision decision, long? messageDbId,
        CancellationToken cancellationToken)
    {
        var alertText = SafetyAlertText.Format(decision, profile.EmergencyPhone);
        await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "completed", "fixed_alert"));
        if (await _replies.ReplyAsync(telegramClient, message, alertText, cancellationToken, quote: true)
            || await _replies.ReplyAsync(telegramClient, message, alertText, cancellationToken, quote: true))
        {
            _logger.LogWarning(
                "Safety alert {RuleKey} ({Level}) for an unsaved value of message {MessageDbId}", decision.RuleKey, decision.Level, messageDbId);
            return true;
        }

        _logger.LogError("Safety alert {RuleKey} for message {MessageDbId} could not be sent", decision.RuleKey, messageDbId);
        return false;
    }

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
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "completed", "fixed_alert"));
            await _replies.ReplyAsync(
                telegramClient, message, SafetyAlertText.FormatNotRecorded(decision, profile.EmergencyPhone), cancellationToken, quote: true);
            _logger.LogWarning("Quick scan alert {RuleKey} ({Level}) for message {MessageDbId}", decision.RuleKey, decision.Level, messageDbId);
            sent = true;
        }

        if (clarify && problem is not null)
        {
            await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.Clarification(problem, text), cancellationToken, quote: true);
            _logger.LogInformation("Quick scan clarification for message {MessageDbId}", messageDbId);
            sent = true;
        }

        return sent;
    }

    private async Task SendFailureNoticeAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, CancellationToken cancellationToken, bool throttled = true)
    {
        if (throttled && !_failureNotices.TryAcquire(bot.TelegramBotId, message.ChatId, message.TopicId, _clock.UtcNow))
        {
            _logger.LogInformation("Extraction failure notice throttled for chat message {MessageId}", message.MessageId);
            return;
        }

        await _replies.ReplyAsync(telegramClient, message, ExtractionReplies.FailureNotice, cancellationToken, quote: true);
    }
}
