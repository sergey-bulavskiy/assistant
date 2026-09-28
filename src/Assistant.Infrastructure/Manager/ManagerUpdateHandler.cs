using System.Data;
using System.Security.Cryptography;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
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

        if (command is not null)
        {
            // A later task adds /settings.
            _logger.LogInformation("manager received an unhandled command: {Command}", command);
            await telegramClient.SendTextAsync(chatId, topicId, "Неизвестная команда.", cancellationToken);
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
            default:
                // A later task adds settings actions (bot_*/settingsplace_*/member_disable|enable|makeowner).
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
