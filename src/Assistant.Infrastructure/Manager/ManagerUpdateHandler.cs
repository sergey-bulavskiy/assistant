using System.Data;
using Assistant.Application.Common;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Assistant.Infrastructure.Manager;

public class ManagerUpdateHandler : IManagerUpdateHandler
{
    private readonly AssistantDbContext _db;
    private readonly IClaimCodeProvider _claimCode;
    private readonly IClock _clock;
    private readonly ILogger<ManagerUpdateHandler> _logger;

    public ManagerUpdateHandler(AssistantDbContext db, IClaimCodeProvider claimCode, IClock clock, ILogger<ManagerUpdateHandler> logger)
    {
        _db = db;
        _claimCode = claimCode;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            // A later task adds real resolution (place/user approvals, settings actions).
            await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Пока не реализовано", cancellationToken);
            return;
        }

        if (update.Message is not { Kind: not Assistant.Domain.Messages.MessageKind.Service, Text: { } text, UserId: { } userId })
        {
            return;
        }

        var chatId = update.Message.ChatId;
        var topicId = update.Message.TopicId;
        var command = CommandParser.Parse(text, managerBot.Username);

        if (command == "claim")
        {
            await HandleClaimAsync(chatId, topicId, userId, update.Message.Username, CommandParser.ParseArgs(text), telegramClient, cancellationToken);
            return;
        }

        if (command is not null)
        {
            // A later task adds /newbot, /settings.
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
}
