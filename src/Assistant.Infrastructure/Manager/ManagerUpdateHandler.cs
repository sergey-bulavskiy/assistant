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
    private readonly UsageCommandHandler _usageHandler;
    private readonly SettingsCommandHandler _settingsHandler;
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
        UsageCommandHandler usageHandler,
        SettingsCommandHandler settingsHandler,
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
        _usageHandler = usageHandler;
        _settingsHandler = settingsHandler;
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
            await _settingsHandler.HandleSettingsAsync(chatId, topicId, userId, telegramClient, cancellationToken);
            return;
        }

        if (command == "usage")
        {
            // /usage can show platform-wide spend and a per-bot/per-model breakdown of the caller's
            // own family -- not something to post into a group the bot is in (unlike /settings, which
            // only lists bots/places/members already visible to that group). Private chats only; a
            // group message is silently ignored, the same as any other unrecognized context would be.
            if (update.Message.ChatType == "private")
            {
                var reply = await _usageHandler.BuildReplyAsync(userId, cancellationToken);
                await telegramClient.SendTextAsync(chatId, topicId, reply, replyToMessageId: null, cancellationToken);
            }

            return;
        }

        if (command is not null)
        {
            _logger.LogInformation("manager received an unhandled command: {Command}", command);
            await telegramClient.SendTextAsync(chatId, topicId, "Неизвестная команда.", replyToMessageId: null, cancellationToken);
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
                await telegramClient.SendTextAsync(chatId, topicId, "Платформа уже активирована.", replyToMessageId: null, cancellationToken);
                return;
            }

            if (code is null || code != _claimCode.Code)
            {
                await transaction.RollbackAsync(cancellationToken);
                await telegramClient.SendTextAsync(chatId, topicId, "Неверный код.", replyToMessageId: null, cancellationToken);
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
            await telegramClient.SendTextAsync(chatId, topicId, "Платформа уже активирована.", replyToMessageId: null, cancellationToken);
            return;
        }

        await telegramClient.SendTextAsync(chatId, topicId, "Готово! Семья создана, вы её владелец. Команда /newbot создаёт бота роли.", replyToMessageId: null, cancellationToken);
    }

    // EF Core's default (non-retrying) execution strategy detects that a serialization failure is
    // "transient" and, because it happened inside our own explicit transaction (BeginTransactionAsync
    // above) rather than one it controls, re-wraps it as an InvalidOperationException instead of
    // letting the PostgresException surface directly — one extra level of nesting on top of the usual
    // DbUpdateException wrapper. Walk the whole chain instead of checking a fixed depth.
    private static bool IsSerializationFailure(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleNewBotAsync(
        long chatId, int? topicId, long userId, string managerUsername, string? role, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        if (role is null)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Укажите роль: /newbot <роль>, например /newbot general.", replyToMessageId: null, cancellationToken);
            return;
        }

        if (role.Length > IPendingBotCreations.MaxRoleLength)
        {
            await telegramClient.SendTextAsync(
                chatId, topicId, $"Роль слишком длинная: не больше {IPendingBotCreations.MaxRoleLength} символов.", replyToMessageId: null, cancellationToken);
            return;
        }

        var isOwner = await _db.FamilyMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.TelegramUserId == userId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (!isOwner)
        {
            await telegramClient.SendTextAsync(chatId, topicId, "Только владелец семьи может создавать ботов.", replyToMessageId: null, cancellationToken);
            return;
        }

        await _pendingBotCreations.SetPendingRoleAsync(userId, role, cancellationToken);

        var suggestedUsername = GenerateSuggestedUsername(role);
        var link = $"https://t.me/newbot/{managerUsername}/{suggestedUsername}?name={Uri.EscapeDataString(role)}";
        await telegramClient.SendTextAsync(chatId, topicId, $"Нажмите, чтобы создать бота роли «{role}»: {link}", replyToMessageId: null, cancellationToken);
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
            default:
                if (SettingsCommandHandler.HandlesCallback(parts[0]))
                {
                    await _settingsHandler.HandleCallbackAsync(callback, parts[0], id, telegramClient, cancellationToken);
                    return;
                }

                await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Пока не реализовано", cancellationToken);
                return;
        }
    }

    private Task<bool> IsApprovedOwnerAsync(long userId, long familyId, CancellationToken cancellationToken) =>
        ManagerOwnership.IsApprovedOwnerAsync(_db, userId, familyId, cancellationToken);

    private async Task HandleManagedBotCreatedAsync(
        long creatorUserId, long newBotUserId, ITelegramClient telegramClient, CancellationToken cancellationToken)
    {
        var creatorMember = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TelegramUserId == creatorUserId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
        if (creatorMember is null)
        {
            _logger.LogWarning("managed_bot update from a creator with no owning family membership.");
            return;
        }

        var role = await _pendingBotCreations.GetRoleAsync(creatorUserId, cancellationToken) ?? "unspecified";

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
        // The pending role is removed only together with saving the bot: if anything above fails,
        // the update is redelivered and the retry still finds the role.
        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            _db.Bots.Add(bot);
            await _db.SaveChangesAsync(cancellationToken);
            await _pendingBotCreations.RemoveAsync(creatorUserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await _coordinator.StartBotAsync(bot.Id, cancellationToken);

        await telegramClient.SendTextAsync(creatorUserId, null, $"Бот @{identity.Username} создан и запущен (роль: {role}).", replyToMessageId: null, cancellationToken);
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
