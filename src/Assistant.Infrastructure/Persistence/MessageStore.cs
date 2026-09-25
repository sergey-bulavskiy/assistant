using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Persistence;

public class MessageStore : IMessageStore
{
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;

    public MessageStore(AssistantDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken)
    {
        var state = await _db.BotStates.FirstOrDefaultAsync(b => b.BotId == identity.Id, cancellationToken);
        var now = _clock.UtcNow;

        if (state is null)
        {
            _db.BotStates.Add(new BotState { BotId = identity.Id, Username = identity.Username, LastUpdateId = 0, UpdatedAt = now });
        }
        else if (state.Username != identity.Username)
        {
            state.Username = identity.Username;
            state.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken)
    {
        var state = await _db.BotStates.AsNoTracking().FirstOrDefaultAsync(b => b.BotId == botId, cancellationToken);
        return state?.LastUpdateId ?? 0;
    }

    public async Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var state = await _db.BotStates.FirstOrDefaultAsync(b => b.BotId == botId, cancellationToken)
            ?? throw new InvalidOperationException($"bot_state row for bot {botId} not found; call EnsureBotStateAsync first.");

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
        state.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new StoreResult(outcome, messageDbId);
    }
}
