using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Persistence;

public class MessageStore : IMessageStore
{
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<MessageStore> _logger;

    public MessageStore(AssistantDbContext db, IClock clock, ILogger<MessageStore> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken)
    {
        var bot = await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.TelegramBotId == identity.Id, cancellationToken);

        if (bot is null)
        {
            _logger.LogWarning(
                "EnsureBotStateAsync: no bots row found for telegram bot id {TelegramBotId}; bot must be registered via claim/newbot before it can store messages.",
                identity.Id);
            return;
        }

        if (bot.Username != identity.Username)
        {
            bot.Username = identity.Username;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken)
    {
        var bot = await _db.Bots.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(b => b.TelegramBotId == botId, cancellationToken);
        return bot?.LastUpdateId ?? 0;
    }

    public async Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var state = await _db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.TelegramBotId == botId, cancellationToken)
            ?? throw new InvalidOperationException($"bots row for telegram bot id {botId} not found; the bot must be registered before polling starts.");

        var now = _clock.UtcNow;

        if (updateId <= state.LastUpdateId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new StoreResult(StoreOutcome.AlreadyProcessed, null);
        }

        StoreOutcome outcome;
        long? messageDbId = null;

        if (message is null)
        {
            outcome = StoreOutcome.OffsetOnly;
        }
        else
        {
            var existing = await _db.Messages.FirstOrDefaultAsync(
                m => m.BotId == botId && m.ChatId == message.ChatId && m.TelegramMessageId == message.MessageId,
                cancellationToken);

            if (existing is null)
            {
                var entity = new StoredMessage
                {
                    BotId = botId,
                    FamilyId = state.FamilyId,
                    ChatId = message.ChatId,
                    TopicId = message.TopicId,
                    TelegramMessageId = message.MessageId,
                    UserId = message.UserId,
                    Username = message.Username,
                    ChatType = message.ChatType,
                    Kind = message.Kind,
                    Text = message.Text,
                    SentAt = message.SentAt,
                    EditedAt = message.EditedAt,
                    Raw = message.RawJson,
                    CreatedAt = now
                };
                _db.Messages.Add(entity);
                await _db.SaveChangesAsync(cancellationToken);
                messageDbId = entity.Id;
                outcome = StoreOutcome.Stored;
            }
            else if (message.IsEdit)
            {
                existing.Text = message.Text;
                existing.EditedAt = message.EditedAt ?? now;
                existing.Raw = message.RawJson;
                await _db.SaveChangesAsync(cancellationToken);
                messageDbId = existing.Id;
                outcome = StoreOutcome.Updated;
            }
            else
            {
                messageDbId = existing.Id;
                outcome = StoreOutcome.Duplicate;
            }

            if (message.MigrateToChatId is { } toChatId)
            {
                var migrationExists = await _db.ChatMigrations.AnyAsync(c => c.FromChatId == message.ChatId, cancellationToken);
                if (!migrationExists)
                {
                    _db.ChatMigrations.Add(new ChatMigration { FromChatId = message.ChatId, ToChatId = toChatId, MigratedAt = now });
                }
            }
        }

        state.LastUpdateId = updateId;
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new StoreResult(outcome, messageDbId);
    }
}
