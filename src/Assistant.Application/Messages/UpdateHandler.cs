using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Manager;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Application.Messages;

public class UpdateHandler
{
    private readonly IMessageStore _store;
    private readonly IApprovalService _approvals;
    private readonly ICurrentFamily _currentFamily;
    private readonly IManagerUpdateHandler _managerHandler;
    private readonly IOptions<BotOptions> _options;
    private readonly BuildInfo _buildInfo;
    private readonly IClock _clock;
    private readonly ILogger<UpdateHandler> _logger;

    public UpdateHandler(
        IMessageStore store,
        IApprovalService approvals,
        ICurrentFamily currentFamily,
        IManagerUpdateHandler managerHandler,
        IOptions<BotOptions> options,
        BuildInfo buildInfo,
        IClock clock,
        ILogger<UpdateHandler> logger)
    {
        _store = store;
        _approvals = approvals;
        _currentFamily = currentFamily;
        _managerHandler = managerHandler;
        _options = options;
        _buildInfo = buildInfo;
        _clock = clock;
        _logger = logger;
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

        if (update.MembershipChange is not null || update.CallbackQuery is not null || update.Message is null)
        {
            // "removed from chat" needs no action beyond advancing the offset; role bots never
            // receive callback queries (their allowed-update list in BotPollingCoordinator has no
            // CallbackQuery) or managed_bot/other non-message updates in practice, but handle them
            // the same inert way defensively.
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

        var result = await _store.StoreAsync(bot.TelegramBotId, update.UpdateId, message, cancellationToken);
        _logger.LogInformation("update {UpdateId} processed with outcome {Outcome}", update.UpdateId, result.Outcome);

        var reply = ReplyPolicy.Decide(message, result, bot.Username, () => VersionText.Format(_buildInfo, _clock.UtcNow));
        if (reply is null)
        {
            return;
        }

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
