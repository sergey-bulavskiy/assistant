using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Manager;

/// <summary>/settings (owner-only listing of bots, places and members with buttons) and every
/// callback those buttons send, except the approval callbacks (place_approve/place_deny,
/// member_allow/member_deny), which stay in ManagerUpdateHandler with the approval DMs. Kept as its
/// own class, invoked from ManagerUpdateHandler, like UsageCommandHandler (M15).</summary>
public class SettingsCommandHandler
{
    // Every callback action this class handles. ManagerUpdateHandler forwards exactly these; a new
    // settings button must be added here, or the dispatcher answers "Пока не реализовано".
    private static readonly HashSet<string> CallbackActions = new(StringComparer.Ordinal)
    {
        "bot_disable",
        "bot_enable",
        "bot_remove",
        "settingsplace_disable",
        "settingsplace_enable",
        "settingsplace_remove",
        "settingsplace_autoreply_on",
        "settingsplace_autoreply_off",
        "settingsplace_healthquestions_on",
        "settingsplace_healthquestions_off",
        "member_disable",
        "member_enable",
        "member_makeowner"
    };

    private readonly AssistantDbContext _db;
    private readonly BotPollingCoordinator _coordinator;
    private readonly IClock _clock;

    public SettingsCommandHandler(AssistantDbContext db, BotPollingCoordinator coordinator, IClock clock)
    {
        _db = db;
        _coordinator = coordinator;
        _clock = clock;
    }

    public static bool HandlesCallback(string action) => CallbackActions.Contains(action);

    public async Task HandleSettingsAsync(long chatId, int? topicId, long userId, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        var caller = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TelegramUserId == userId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (caller is null)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Только владелец семьи может использовать /settings.", replyToMessageId: null, cancellationToken);
            return;
        }

        var familyId = caller.FamilyId;

        var bots = await _db.Bots.IgnoreQueryFilters().Where(b => b.FamilyId == familyId).ToListAsync(cancellationToken);
        if (bots.Count == 0)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Боты: нет.", replyToMessageId: null, cancellationToken);
        }
        foreach (var bot in bots)
        {
            var statusLabel = bot.Status == BotStatus.Active ? "активен" : "отключен";
            var toggleButton = bot.Status == BotStatus.Active
                ? new InlineButton("Отключить", $"bot_disable:{bot.Id}")
                : new InlineButton("Включить", $"bot_enable:{bot.Id}");
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"Бот @{bot.Username} (роль {bot.Role}): {statusLabel}",
                new[] { toggleButton, new InlineButton("Удалить", $"bot_remove:{bot.Id}") }, replyToMessageId: null, cancellationToken);
        }

        var botIds = bots.Select(b => b.Id).ToList();
        var places = await _db.Places.IgnoreQueryFilters().Where(p => botIds.Contains(p.BotId)).ToListAsync(cancellationToken);
        if (places.Count == 0)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Места: нет.", replyToMessageId: null, cancellationToken);
        }
        foreach (var place in places)
        {
            var statusLabel = place.Status switch
            {
                PlaceStatus.Approved => "активно",
                PlaceStatus.Disabled => "отключено",
                PlaceStatus.Denied => "отклонено",
                _ => "ожидает"
            };
            var toggleButton = place.Status == PlaceStatus.Disabled
                ? new InlineButton("Включить", $"settingsplace_enable:{place.Id}")
                : new InlineButton("Отключить", $"settingsplace_disable:{place.Id}");
            var buttons = new List<InlineButton> { toggleButton, new InlineButton("Удалить", $"settingsplace_remove:{place.Id}") };
            // The button carries the target state, so repeated taps do not flip it back.
            var placeBot = bots.Single(b => b.Id == place.BotId);
            if (BotRoles.IsGeneral(placeBot.Role))
            {
                buttons.Add(place.ReplyToAll
                    ? new InlineButton("Отвечать на все: вкл", $"settingsplace_autoreply_off:{place.Id}")
                    : new InlineButton("Отвечать на все: выкл", $"settingsplace_autoreply_on:{place.Id}"));
            }
            else if (BotRoles.IsHealth(placeBot.Role))
            {
                buttons.Add(place.ReplyToAll
                    ? new InlineButton("Отвечать без упоминания: вкл", $"settingsplace_healthquestions_off:{place.Id}")
                    : new InlineButton("Отвечать без упоминания: выкл", $"settingsplace_healthquestions_on:{place.Id}"));
            }

            // Topics of one chat share its title; the topic id tells them apart.
            var topicSuffix = place.TopicId is { } placeTopicId ? $" (тема {placeTopicId})" : string.Empty;
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"Место «{place.Title}»{topicSuffix} (@{placeBot.Username}): {statusLabel}", buttons, replyToMessageId: null, cancellationToken);
        }

        var members = await _db.FamilyMembers.IgnoreQueryFilters().Where(m => m.FamilyId == familyId).ToListAsync(cancellationToken);
        foreach (var member in members)
        {
            var statusLabel = member.Status switch
            {
                FamilyMemberStatus.Approved => "активен",
                FamilyMemberStatus.Denied => "отключен",
                _ => "ожидает"
            };
            // A pending member gets the same Allow/Deny as the approval DM, resolved through the
            // same path, so the DM's buttons are closed too. Ownership is offered only once
            // approved, since it grants nothing before that.
            var buttons = member.Status switch
            {
                FamilyMemberStatus.Pending => new[]
                {
                    new InlineButton("Разрешить", $"member_allow:{member.Id}"),
                    new InlineButton("Отклонить", $"member_deny:{member.Id}")
                },
                FamilyMemberStatus.Denied => new[] { new InlineButton("Включить", $"member_enable:{member.Id}") },
                _ when member.IsOwner => new[] { new InlineButton("Отключить", $"member_disable:{member.Id}") },
                _ => new[]
                {
                    new InlineButton("Отключить", $"member_disable:{member.Id}"),
                    new InlineButton("Сделать владельцем", $"member_makeowner:{member.Id}")
                }
            };
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"{member.DisplayName}{(member.IsOwner ? " (владелец)" : string.Empty)}: {statusLabel}",
                buttons, replyToMessageId: null, cancellationToken);
        }
    }

    /// <summary>Handles one of <see cref="CallbackActions"/>; ManagerUpdateHandler has already parsed
    /// "action:id" and checked that id is a number.</summary>
    public async Task HandleCallbackAsync(CallbackQueryInfo callback, string action, long id, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "bot_disable":
            case "bot_enable":
            {
                var bot = await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                // A null FamilyId means this is the manager bot itself — never toggleable via this
                // family-scoped path, so it is treated the same as "not authorized".
                if (bot is null || bot.FamilyId is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, bot.FamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                bot.Status = action == "bot_enable" ? BotStatus.Active : BotStatus.Disabled;
                await _db.SaveChangesAsync(cancellationToken);
                if (action == "bot_enable")
                {
                    await _coordinator.StartBotAsync(id, cancellationToken);
                }
                else
                {
                    await _coordinator.StopBotAsync(id);
                }
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Готово.", cancellationToken);
                return;
            }
            case "bot_remove":
            {
                var bot = await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                if (bot is null || bot.FamilyId is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, bot.FamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                await _coordinator.StopBotAsync(id);
                // No FK between places.bot_id and bots.id (family-scoped tables reference their
                // parent loosely, like FamilyMember does with family_id), so removing the bot alone
                // would leave its places as permanently orphaned rows — delete them together.
                var places = await _db.Places.IgnoreQueryFilters().Where(p => p.BotId == id).ToListAsync(cancellationToken);
                _db.Places.RemoveRange(places);
                _db.Bots.Remove(bot);
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Бот удалён.", cancellationToken);
                return;
            }
            case "settingsplace_disable":
            case "settingsplace_enable":
            {
                var place = await _db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                var placeFamilyId = place is null
                    ? null
                    : await _db.Bots.IgnoreQueryFilters().Where(b => b.Id == place.BotId).Select(b => (long?)b.FamilyId).FirstOrDefaultAsync(cancellationToken);
                if (place is null || placeFamilyId is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, placeFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                place.Status = action == "settingsplace_enable" ? PlaceStatus.Approved : PlaceStatus.Disabled;
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Готово.", cancellationToken);
                return;
            }
            case "settingsplace_remove":
            {
                var place = await _db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                var placeFamilyId = place is null
                    ? null
                    : await _db.Bots.IgnoreQueryFilters().Where(b => b.Id == place.BotId).Select(b => (long?)b.FamilyId).FirstOrDefaultAsync(cancellationToken);
                if (place is null || placeFamilyId is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, placeFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                _db.Places.Remove(place);
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Место удалено.", cancellationToken);
                return;
            }
            case "settingsplace_autoreply_on":
            case "settingsplace_autoreply_off":
            case "settingsplace_healthquestions_on":
            case "settingsplace_healthquestions_off":
            {
                var place = await _db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                var bot = place is null
                    ? null
                    : await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == place.BotId, cancellationToken);
                if (place is null || bot is null || bot.FamilyId is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, bot.FamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                // Server-side too: callback data is guessable, the button alone proves nothing.
                var healthQuestions = action.StartsWith("settingsplace_healthquestions_", StringComparison.Ordinal);
                if (healthQuestions ? !BotRoles.IsHealth(bot.Role) : !BotRoles.IsGeneral(bot.Role))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId,
                        healthQuestions ? "Доступно только для бота health." : "Доступно только для бота general.", cancellationToken);
                    return;
                }

                var turnOn = action is "settingsplace_autoreply_on" or "settingsplace_healthquestions_on";
                place.ReplyToAll = turnOn;
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(
                    callback.CallbackQueryId,
                    healthQuestions
                        ? turnOn ? "Готово: отвечаю без упоминания, когда сообщение ожидает ответа." : "Готово: отвечаю только при обращении."
                        : turnOn ? "Готово: отвечаю на все сообщения." : "Готово: отвечаю только на обращения.",
                    cancellationToken);
                return;
            }
            case "member_disable":
            case "member_enable":
            {
                var member = await _db.FamilyMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (member is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, member.FamilyId, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                member.Status = action == "member_enable" ? FamilyMemberStatus.Approved : FamilyMemberStatus.Denied;
                member.UpdatedAt = _clock.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Готово.", cancellationToken);
                return;
            }
            case "member_makeowner":
            {
                var member = await _db.FamilyMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (member is null || !await ManagerOwnership.IsApprovedOwnerAsync(_db, callback.FromUserId, member.FamilyId, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                // Does not check member.Status: a still-Pending/Denied member can be flagged
                // IsOwner = true here, but that's inert on its own — IsApprovedOwnerAsync (the only
                // place ownership actually grants anything) separately requires
                // Status == Approved, so promotion has no effect until the member is also approved.
                member.IsOwner = true;
                member.UpdatedAt = _clock.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Теперь владелец.", cancellationToken);
                return;
            }
            default:
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Пока не реализовано", cancellationToken);
                return;
        }
    }
}
