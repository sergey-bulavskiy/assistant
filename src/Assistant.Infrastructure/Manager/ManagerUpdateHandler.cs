using System.Data;
using System.Security.Cryptography;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Assistant.Infrastructure.Manager;

public class ManagerUpdateHandler : IManagerUpdateHandler
{
    private readonly AssistantDbContext _db;
    private readonly IClaimCodeProvider _claimCode;
    private readonly IPendingBotCreations _pendingBotCreations;
    private readonly ITelegramClientFactory _clientFactory;
    private readonly ITokenEncryptor _tokenEncryptor;
    private readonly BotPollingCoordinator _coordinator;
    private readonly IApprovalService _approvals;
    private readonly IClock _clock;
    private readonly ILogger<ManagerUpdateHandler> _logger;

    public ManagerUpdateHandler(
        AssistantDbContext db,
        IClaimCodeProvider claimCode,
        IPendingBotCreations pendingBotCreations,
        ITelegramClientFactory clientFactory,
        ITokenEncryptor tokenEncryptor,
        BotPollingCoordinator coordinator,
        IApprovalService approvals,
        IClock clock,
        ILogger<ManagerUpdateHandler> logger)
    {
        _db = db;
        _claimCode = claimCode;
        _pendingBotCreations = pendingBotCreations;
        _clientFactory = clientFactory;
        _tokenEncryptor = tokenEncryptor;
        _coordinator = coordinator;
        _approvals = approvals;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
    {
        if (update.ManagedBotCreatorUserId is { } creatorUserId && update.ManagedBotUserId is { } newBotUserId)
        {
            await HandleManagedBotCreatedAsync(creatorUserId, newBotUserId, telegramClient, cancellationToken);
            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, telegramClient, cancellationToken);
            return;
        }

        if (update.Message is not { Kind: not Assistant.Domain.Messages.MessageKind.Service, Text: { } text, UserId: { } userId })
        {
            return;
        }

        var chatId = update.Message.ChatId;
        var topicId = update.Message.TopicId;
        var username = update.Message.Username;
        var command = CommandParser.Parse(text, managerBot.Username);

        if (command == "claim")
        {
            await HandleClaimAsync(chatId, topicId, userId, username, CommandParser.ParseArgs(text), telegramClient, cancellationToken);
            return;
        }

        if (command == "newbot")
        {
            await HandleNewBotAsync(chatId, topicId, userId, managerBot.Username, CommandParser.ParseArgs(text), telegramClient, cancellationToken);
            return;
        }

        if (command == "settings")
        {
            await HandleSettingsAsync(chatId, topicId, userId, telegramClient, cancellationToken);
            return;
        }

        if (command is not null)
        {
            _logger.LogInformation("manager received an unhandled command: {Command}", command);
            await telegramClient.SendTextAsync(chatId, topicId, "Неизвестная команда.", cancellationToken);
        }
    }

    private async Task HandleSettingsAsync(long chatId, int? topicId, long userId, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        var caller = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TelegramUserId == userId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (caller is null)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Только владелец семьи может использовать /settings.", cancellationToken);
            return;
        }

        var familyId = caller.FamilyId;

        var bots = await _db.Bots.IgnoreQueryFilters().Where(b => b.FamilyId == familyId).ToListAsync(cancellationToken);
        if (bots.Count == 0)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Боты: нет.", cancellationToken);
        }
        foreach (var bot in bots)
        {
            var statusLabel = bot.Status == BotStatus.Active ? "активен" : "отключен";
            var toggleButton = bot.Status == BotStatus.Active
                ? new InlineButton("Отключить", $"bot_disable:{bot.Id}")
                : new InlineButton("Включить", $"bot_enable:{bot.Id}");
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"Бот @{bot.Username} (роль {bot.Role}): {statusLabel}",
                new[] { toggleButton, new InlineButton("Удалить", $"bot_remove:{bot.Id}") }, cancellationToken);
        }

        var botIds = bots.Select(b => b.Id).ToList();
        var places = await _db.Places.IgnoreQueryFilters().Where(p => botIds.Contains(p.BotId)).ToListAsync(cancellationToken);
        if (places.Count == 0)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Места: нет.", cancellationToken);
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
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"Место «{place.Title}»: {statusLabel}",
                new[] { toggleButton, new InlineButton("Удалить", $"settingsplace_remove:{place.Id}") }, cancellationToken);
        }

        var members = await _db.FamilyMembers.IgnoreQueryFilters().Where(m => m.FamilyId == familyId).ToListAsync(cancellationToken);
        foreach (var member in members)
        {
            var statusLabel = member.Status == FamilyMemberStatus.Denied ? "отключен" : "активен";
            var toggleButton = member.Status == FamilyMemberStatus.Denied
                ? new InlineButton("Включить", $"member_enable:{member.Id}")
                : new InlineButton("Отключить", $"member_disable:{member.Id}");
            var buttons = member.IsOwner
                ? new[] { toggleButton }
                : new[] { toggleButton, new InlineButton("Сделать владельцем", $"member_makeowner:{member.Id}") };
            await telegramClient.SendTextWithButtonsAsync(
                chatId, topicId, $"{member.DisplayName}{(member.IsOwner ? " (владелец)" : string.Empty)}: {statusLabel}",
                buttons, cancellationToken);
        }
    }

    private async Task HandleClaimAsync(
        long chatId, int? topicId, long userId, string? username, string? code, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        // The check-then-act below (AnyAsync, then insert) is a classic TOCTOU: two concurrent
        // /claim calls could both observe "no family yet" before either commits. Serializable
        // isolation makes Postgres detect that overlap and abort one side with a serialization
        // failure (SqlState 40001), which is caught below and treated the same as "already
        // claimed". This keeps the invariant scoped to the claim path (no schema-level "at most
        // one family, ever" constraint), since M3+ is expected to allow multiple families.
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        try
        {
            var alreadyClaimed = await _db.Families.IgnoreQueryFilters().AnyAsync(cancellationToken);
            if (alreadyClaimed)
            {
                await transaction.RollbackAsync(cancellationToken);
                await telegramClient.SendTextAsync(chatId, topicId, "Платформа уже активирована.", cancellationToken);
                return;
            }

            if (code is null || code != _claimCode.Code)
            {
                await transaction.RollbackAsync(cancellationToken);
                await telegramClient.SendTextAsync(chatId, topicId, "Неверный код.", cancellationToken);
                return;
            }

            var now = _clock.UtcNow;
            var family = new Family { Name = "Family 1", CreatedAt = now };
            _db.Families.Add(family);
            await _db.SaveChangesAsync(cancellationToken);

            _db.FamilyMembers.Add(new FamilyMember
            {
                FamilyId = family.Id,
                TelegramUserId = userId,
                DisplayName = username ?? $"user {userId}",
                Username = username,
                Status = FamilyMemberStatus.Approved,
                IsOwner = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            _logger.LogInformation("claim lost a concurrent race (serialization failure); treating as already claimed.");
            await telegramClient.SendTextAsync(chatId, topicId, "Платформа уже активирована.", cancellationToken);
            return;
        }

        await telegramClient.SendTextAsync(chatId, topicId, "Готово! Семья создана, вы её владелец. Команда /newbot создаёт бота роли.", cancellationToken);
    }

    private static bool IsSerializationFailure(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
        || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure };

    private async Task HandleNewBotAsync(
        long chatId, int? topicId, long userId, string managerUsername, string? role, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        if (role is null)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Укажите роль: /newbot <роль>, например /newbot general.", cancellationToken);
            return;
        }

        var isOwner = await _db.FamilyMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.TelegramUserId == userId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (!isOwner)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Только владелец семьи может создавать ботов.", cancellationToken);
            return;
        }

        _pendingBotCreations.SetPendingRole(userId, role);

        var suggestedUsername = GenerateSuggestedUsername(role);
        var link = $"https://t.me/newbot/{managerUsername}/{suggestedUsername}?name={Uri.EscapeDataString(role)}";
        await telegramClient.SendTextAsync(chatId, topicId, $"Нажмите, чтобы создать бота роли «{role}»: {link}", cancellationToken);
    }

    private async Task HandleCallbackAsync(CallbackQueryInfo callback, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        var parts = callback.Data.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], out var id))
        {
            await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Некорректные данные.", cancellationToken);
            return;
        }

        switch (parts[0])
        {
            case "place_approve":
            case "place_deny":
            {
                var placeFamilyId = await _db.Places.IgnoreQueryFilters()
                    .Where(p => p.Id == id)
                    .Join(_db.Bots.IgnoreQueryFilters(), p => p.BotId, b => b.Id, (p, b) => b.FamilyId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (placeFamilyId is null)
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Уже решено.", cancellationToken);
                    return;
                }

                if (!await IsApprovedOwnerAsync(callback.FromUserId, placeFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                var resolution = await _approvals.ResolvePlaceApprovalAsync(id, approve: parts[0] == "place_approve", cancellationToken);
                await telegramClient.AnswerCallbackAsync(
                    callback.CallbackQueryId, resolution == ApprovalResolution.Applied ? "Записано." : "Уже решено.", cancellationToken);
                return;
            }
            case "member_allow":
            case "member_deny":
            {
                var memberFamilyId = await _db.FamilyMembers.IgnoreQueryFilters()
                    .Where(m => m.Id == id)
                    .Select(m => (long?)m.FamilyId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (memberFamilyId is null)
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Уже решено.", cancellationToken);
                    return;
                }

                if (!await IsApprovedOwnerAsync(callback.FromUserId, memberFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                var resolution = await _approvals.ResolveUserApprovalAsync(id, approve: parts[0] == "member_allow", cancellationToken);
                await telegramClient.AnswerCallbackAsync(
                    callback.CallbackQueryId, resolution == ApprovalResolution.Applied ? "Записано." : "Уже решено.", cancellationToken);
                return;
            }
            case "bot_disable":
            case "bot_enable":
            {
                var bot = await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                // A null FamilyId means this is the manager bot itself — never toggleable via this
                // family-scoped path, so it is treated the same as "not authorized".
                if (bot is null || bot.FamilyId is null || !await IsApprovedOwnerAsync(callback.FromUserId, bot.FamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                bot.Status = parts[0] == "bot_enable" ? BotStatus.Active : BotStatus.Disabled;
                await _db.SaveChangesAsync(cancellationToken);
                if (parts[0] == "bot_enable")
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
                if (bot is null || bot.FamilyId is null || !await IsApprovedOwnerAsync(callback.FromUserId, bot.FamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                await _coordinator.StopBotAsync(id);
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
                if (place is null || placeFamilyId is null || !await IsApprovedOwnerAsync(callback.FromUserId, placeFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                place.Status = parts[0] == "settingsplace_enable" ? PlaceStatus.Approved : PlaceStatus.Disabled;
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
                if (place is null || placeFamilyId is null || !await IsApprovedOwnerAsync(callback.FromUserId, placeFamilyId.Value, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                _db.Places.Remove(place);
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Место удалено.", cancellationToken);
                return;
            }
            case "member_disable":
            case "member_enable":
            {
                var member = await _db.FamilyMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (member is null || !await IsApprovedOwnerAsync(callback.FromUserId, member.FamilyId, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

                member.Status = parts[0] == "member_enable" ? FamilyMemberStatus.Approved : FamilyMemberStatus.Denied;
                member.UpdatedAt = _clock.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Готово.", cancellationToken);
                return;
            }
            case "member_makeowner":
            {
                var member = await _db.FamilyMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (member is null || !await IsApprovedOwnerAsync(callback.FromUserId, member.FamilyId, cancellationToken))
                {
                    await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "У вас нет прав.", cancellationToken);
                    return;
                }

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

    // Approval-button taps carry only a guessable sequential id (place_approve:1, member_allow:2, …),
    // so the button data itself proves nothing about who is tapping. The DM was sent to the owning
    // family's owners, so resolving it must be gated on the tapping user actually being one of them —
    // not just an owner of some other family.
    private async Task<bool> IsApprovedOwnerAsync(long userId, long familyId, CancellationToken cancellationToken) =>
        await _db.FamilyMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.TelegramUserId == userId && m.FamilyId == familyId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);

    private async Task HandleManagedBotCreatedAsync(
        long creatorUserId, long newBotUserId, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        var creatorMember = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TelegramUserId == creatorUserId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (creatorMember is null)
        {
            _logger.LogWarning("managed_bot update from a creator with no owning family membership: {CreatorUserId}", creatorUserId);
            return;
        }

        var role = _pendingBotCreations.TakeRole(creatorUserId) ?? "unspecified";

        var token = await telegramClient.GetManagedBotTokenAsync(newBotUserId, cancellationToken);
        var newBotClient = _clientFactory.Create(token);
        var identity = await newBotClient.GetMeAsync(cancellationToken);

        var bot = new Domain.Bots.Bot
        {
            FamilyId = creatorMember.FamilyId,
            TelegramBotId = identity.Id,
            Username = identity.Username,
            Role = role,
            TokenEncrypted = _tokenEncryptor.Encrypt(token),
            Status = BotStatus.Active,
            LastUpdateId = 0,
            CreatedAt = _clock.UtcNow
        };
        _db.Bots.Add(bot);
        await _db.SaveChangesAsync(cancellationToken);

        await _coordinator.StartBotAsync(bot.Id, cancellationToken);

        await telegramClient.SendTextAsync(creatorUserId, null, $"Бот @{identity.Username} создан и запущен (роль: {role}).", cancellationToken);
    }

    // Telegram bot usernames: 5-32 chars, letters/digits/underscores, must end with "bot". This is
    // only a *suggestion* shown to the owner in Telegram's own creation UI, which lets them edit it
    // before confirming — it does not need to be guaranteed unique here.
    private static string GenerateSuggestedUsername(string role)
    {
        var sanitizedRole = new string(role.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (sanitizedRole.Length == 0)
        {
            sanitizedRole = "role";
        }

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var baseName = $"af{sanitizedRole}{suffix}";
        var maxBaseLength = 32 - "bot".Length;
        if (baseName.Length > maxBaseLength)
        {
            baseName = baseName[..maxBaseLength];
        }

        return $"{baseName}bot";
    }
}
