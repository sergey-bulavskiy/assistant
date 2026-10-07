using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Manager;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Application.Messages;

public class UpdateHandler
{
    public const string NoRightsText = "У вас нет прав.";

    private readonly IMessageStore _store;
    private readonly IApprovalService _approvals;
    private readonly ICurrentFamily _currentFamily;
    private readonly IManagerUpdateHandler _managerHandler;
    private readonly IGeneralAssistant _generalAssistant;
    private readonly IHealthAssistant _healthAssistant;
    private readonly IOptions<BotOptions> _options;
    private readonly BuildInfo _buildInfo;
    private readonly IClock _clock;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly ITraceSession _trace;
    private readonly IVetAssistant? _vetAssistant;

    public UpdateHandler(
        IMessageStore store,
        IApprovalService approvals,
        ICurrentFamily currentFamily,
        IManagerUpdateHandler managerHandler,
        IGeneralAssistant generalAssistant,
        IHealthAssistant healthAssistant,
        IOptions<BotOptions> options,
        BuildInfo buildInfo,
        IClock clock,
        ILogger<UpdateHandler> logger,
        ITraceSession? trace = null,
        IVetAssistant? vetAssistant = null)
    {
        _store = store;
        _approvals = approvals;
        _currentFamily = currentFamily;
        _managerHandler = managerHandler;
        _generalAssistant = generalAssistant;
        _healthAssistant = healthAssistant;
        _options = options;
        _buildInfo = buildInfo;
        _clock = clock;
        _logger = logger;
        _trace = trace ?? NullTraceSession.Instance;
        _vetAssistant = vetAssistant;
    }

    public async Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
    {
        _currentFamily.Set(bot.FamilyId);

        if (bot.FamilyId is null)
        {
            await _managerHandler.HandleAsync(bot, telegramClient, update, cancellationToken);
            await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
            return;
        }

        // Bot just added to a chat: request a whole-chat place approval immediately,
        // without waiting for a first message. topic_id is always null here — Telegram's
        // my_chat_member update carries no topic information.
        if (update.MembershipChange is { IsNowMember: true } added)
        {
            await _approvals.GetOrCreatePendingPlaceAsync(bot.BotDbId, added.ChatId, null, added.ChatTitle, cancellationToken);
            await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
            return;
        }

        // A button tap: handled first, then the offset advances (a failure retries the update).
        if (update.CallbackQuery is { } callback)
        {
            try
            {
                await HandleCallbackAsync(bot, telegramClient, callback, update.UpdateId, cancellationToken);
                await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
                await TraceSafety.RecordAsync(_trace, new TraceEventData("interaction", "completed", "normal"));
            }
            catch
            {
                await TraceSafety.RecordAsync(_trace, new TraceEventData("interaction", "failed", "normal"));
                throw;
            }
            return;
        }

        if (update.MembershipChange is not null || update.Message is null)
        {
            // "removed from chat" needs no action beyond advancing the offset; role bots never
            // receive managed_bot or other non-message updates in practice, but handle them the
            // same inert way defensively.
            await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
            return;
        }

        var message = update.Message;

        // Approval gating applies to every message kind, including Service (member joined/left,
        // pinned message, chat migrated, …) — an unapproved or denied place/user must not have any
        // of its messages persisted, service messages included, since they can carry names and
        // other content (e.g. new_chat_members) in their raw payload just like a text message.
        //
        // Place approval (spec §3.3) exists for group chats/topics being added to a family; a
        // private DM to a bot has no separate "place" to approve — approving the user (below)
        // already covers all of that family's bots (§3.4), so private chats skip straight to the
        // user-approval gate instead of also requiring a place approval first.
        var replyToAll = false;
        if (message.ChatType != "private")
        {
            var placeTitle = message.ChatTitle ?? $"chat {message.ChatId}";
            var placeId = await _approvals.GetOrCreatePendingPlaceAsync(bot.BotDbId, message.ChatId, message.TopicId, placeTitle, cancellationToken);
            var placeStatus = await _approvals.GetPlaceStatusAsync(placeId, cancellationToken);
            if (placeStatus != PlaceStatus.Approved)
            {
                _logger.LogInformation("ignored message: place not approved ({PlaceStatus})", placeStatus);
                await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
                return;
            }

            // Use this approved place's own setting, never a parent or sibling topic's setting.
            if (BotRoles.IsGeneral(bot.Role) || BotRoles.IsHealth(bot.Role) || BotRoles.IsVet(bot.Role))
            {
                replyToAll = await _approvals.GetPlaceReplyToAllAsync(placeId, cancellationToken);
            }
        }

        if (message.UserId is { } userId && bot.FamilyId is { } familyId)
        {
            var displayName = message.Username ?? $"user {userId}";
            var memberId = await _approvals.GetOrCreatePendingFamilyMemberAsync(
                familyId, userId, displayName, message.Username, bot.Username, cancellationToken);
            var memberStatus = await _approvals.GetFamilyMemberStatusAsync(memberId, cancellationToken);
            if (memberStatus != FamilyMemberStatus.Approved)
            {
                _logger.LogInformation("ignored message: user not approved ({MemberStatus})", memberStatus);
                await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, null, cancellationToken);
                return;
            }
        }

        VetAdmittedSource? admitted = null;
        StoreResult result;
        try
        {
            if (BotRoles.IsVet(bot.Role) && _vetAssistant is not null)
                admitted = await _vetAssistant.AdmitAsync(bot, message, update.UpdateId, cancellationToken);
            result = await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, message, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && BotRoles.IsVet(bot.Role)
            && _vetAssistant is not null && message.UserId is not null && message.Text is { Length: > 0 }
            && VetIntakePersistenceException.IsRetryable(ex))
        {
            _logger.LogWarning("Vet intake persistence failed: {ExceptionType}", ex.GetType().Name);
            throw new VetIntakePersistenceException();
        }
        _logger.LogInformation("update {UpdateId} processed with outcome {Outcome}", update.UpdateId, result.Outcome);

        await TraceSafety.StartAsync(_trace, new TraceStart(
            Guid.NewGuid(), bot.FamilyId.Value, bot.TelegramBotId, message.ChatId, message.TopicId,
            update.UpdateId, result.MessageDbId, message.IsEdit, message.Text, message.Kind.ToString().ToLowerInvariant(),
            _buildInfo.Sha));

        telegramClient = TraceSafety.Wrap(_trace, telegramClient);
        if (result.Outcome is StoreOutcome.AlreadyProcessed or StoreOutcome.Duplicate or StoreOutcome.OffsetOnly)
        {
            var reason = result.Outcome switch
            {
                StoreOutcome.AlreadyProcessed => "already_processed",
                StoreOutcome.Duplicate => "duplicate",
                _ => "offset_only"
            };
            await TraceSafety.RecordAsync(_trace, new TraceEventData("decision", "skipped", reason));
        }

        try
        {
            if (BotRoles.IsGeneral(bot.Role))
            {
                await _generalAssistant.HandleAsync(bot, telegramClient, message, result, cancellationToken, replyToAll);
            }
            else if (BotRoles.IsHealth(bot.Role))
            {
                await _healthAssistant.HandleAsync(bot, telegramClient, message, result, cancellationToken, replyToAll);
            }
            else if (BotRoles.IsVet(bot.Role) && _vetAssistant is not null)
            {
                await _vetAssistant.HandleAsync(bot, telegramClient, message, result, admitted, cancellationToken, replyToAll);
            }
            else
            {
                var reply = ReplyPolicy.Decide(message, result, bot.Username, () => VersionText.Format(_buildInfo, _clock.UtcNow));
                if (reply is not null)
                {
                    try
                    {
                        await telegramClient.SendTextAsync(message.ChatId, message.TopicId, reply, replyToMessageId: null, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("failed to send reply: {ExceptionType}", ex.GetType().Name);
                    }
                }
            }

            await TraceSafety.RecordAsync(_trace, new TraceEventData("interaction", "completed", "normal"));
        }
        catch
        {
            await TraceSafety.RecordAsync(_trace, new TraceEventData("interaction", "failed", "normal"));
            throw;
        }
    }

    public async Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken)
    {
        if (!BotRoles.IsVet(bot.Role) || bot.FamilyId is null || _vetAssistant is null) return;
        _currentFamily.Set(bot.FamilyId);
        await _vetAssistant.ResumeAsync(bot, client, cancellationToken);
    }

    // Role-bot button taps. Health and Vet have buttons; any other role answers the callback with
    // no text so the client stops spinning. Every tap re-checks the database and creates nothing: the
    // tapping user must be an approved member of the bot's family (any member, not only owners), and
    // outside private chats the place (chat/topic) must be approved.
    private async Task HandleCallbackAsync(
        ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, long updateId, CancellationToken cancellationToken)
    {
        if ((!BotRoles.IsHealth(bot.Role) && !BotRoles.IsVet(bot.Role)) || bot.FamilyId is not { } familyId
            || BotRoles.IsVet(bot.Role) && (callback.MessageId <= 0 || callback.MessageChatId == 0
                || callback.MessageChatType is not ("private" or "group" or "supergroup")))
        {
            await AnswerCallbackAsync(telegramClient, callback, null, cancellationToken);
            return;
        }

        var memberStatus = await _approvals.FindFamilyMemberStatusAsync(familyId, callback.FromUserId, cancellationToken);
        var placeApproved = callback.MessageChatType == "private"
            || await _approvals.FindPlaceStatusAsync(bot.BotDbId, callback.MessageChatId, callback.MessageTopicId, cancellationToken)
                == PlaceStatus.Approved;
        if (memberStatus != FamilyMemberStatus.Approved || !placeApproved)
        {
            _logger.LogInformation("callback refused: member {MemberStatus}, place approved {PlaceApproved}", memberStatus, placeApproved);
            await AnswerCallbackAsync(telegramClient, callback, NoRightsText, cancellationToken);
            return;
        }

        await TraceSafety.StartAsync(_trace, new TraceStart(
            Guid.NewGuid(), familyId, bot.TelegramBotId, callback.MessageChatId, callback.MessageTopicId,
            updateId, null, false, null, "callback", _buildInfo.Sha));
        telegramClient = TraceSafety.Wrap(_trace, telegramClient);
        if (BotRoles.IsHealth(bot.Role))
            await _healthAssistant.HandleCallbackAsync(bot, telegramClient, callback, cancellationToken);
        else if (_vetAssistant is not null)
            await _vetAssistant.HandleCallbackAsync(bot, telegramClient, callback, cancellationToken);
        else await AnswerCallbackAsync(telegramClient, callback, null, cancellationToken);
    }

    private async Task AnswerCallbackAsync(
        ITelegramClient telegramClient, CallbackQueryInfo callback, string? text, CancellationToken cancellationToken)
    {
        try
        {
            await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, text, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("failed to answer a callback: {ExceptionType}", ex.GetType().Name);
        }
    }
}
