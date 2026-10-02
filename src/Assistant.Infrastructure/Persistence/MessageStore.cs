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

    public async Task<bool> RebaseOffsetIfIdleAsync(long botId, DateTimeOffset idleBefore, CancellationToken cancellationToken)
    {
        // One conditional UPDATE: no read-then-write race with StoreAsync. A row already at 0 is
        // left alone, so an idle bot's every poll does not rewrite it.
        var rows = await _db.Bots.IgnoreQueryFilters()
            .Where(b => b.TelegramBotId == botId
                && b.LastUpdateId != 0
                && (b.LastUpdateAt == null || b.LastUpdateAt < idleBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastUpdateId, 0L), cancellationToken);
        return rows > 0;
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
            var existing = await _db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(
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
        state.LastUpdateAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new StoreResult(outcome, messageDbId);
    }

    public async Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken)
    {
        var bot = await _db.Bots.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(b => b.TelegramBotId == botId, cancellationToken)
            ?? throw new InvalidOperationException($"bots row for telegram bot id {botId} not found.");

        var entity = new StoredMessage
        {
            BotId = botId,
            FamilyId = bot.FamilyId,
            Direction = MessageDirection.Out,
            ChatId = chatId,
            TopicId = topicId,
            TelegramMessageId = telegramMessageId,
            UserId = null,
            Username = null,
            ChatType = chatType,
            Kind = MessageKind.Text,
            Text = text,
            SentAt = _clock.UtcNow,
            Raw = "{}", // spec §8.2: outgoing rows get raw = '{}', not null/empty -- there is no Telegram update to store.
            CreatedAt = _clock.UtcNow
        };
        var entry = _db.Messages.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A failed SaveChanges (e.g. a unique-index violation) must not leave this entry tracked:
            // the same DbContext/scope is reused for the rest of the request (e.g. the next split
            // part's own StoreOutgoingAsync call), and an EF Core DbContext re-attempts every
            // still-tracked Added entity on its next SaveChangesAsync, which would otherwise repeat
            // this same failure forever and take down unrelated writes with it.
            entry.State = EntityState.Detached;
            throw;
        }
        // Deliberately does NOT touch bot state / LastUpdateId: outgoing replies never advance the
        // bot's Telegram offset (spec §8.2) -- only StoreAsync, for inbound updates, does that.
    }

    public async Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, long? beforeMessageId, int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            return Array.Empty<ContextMessage>();
        }

        // No IgnoreQueryFilters(): UpdateHandler always calls ICurrentFamily.Set(bot.FamilyId) before
        // this runs (spec §2.4), and every row this bot writes (StoreAsync/StoreOutgoingAsync) carries
        // that same FamilyId, so StoredMessage's query filter (FamilyId == null || == current family)
        // already admits every row that matches BotId/ChatId/TopicId below -- it filters out nothing
        // this method should return.
        var query = _db.Messages.AsNoTracking()
            .Where(m => m.BotId == botId && m.ChatId == chatId && m.TopicId == topicId
                && m.Kind == MessageKind.Text
                && !m.Text!.StartsWith("/")); // commands (e.g. /new, /model) are never LLM context (spec §2.4)

        if (afterMessageId is { } afterId)
        {
            query = query.Where(m => m.Id > afterId);
        }

        if (beforeMessageId is { } beforeId)
        {
            query = query.Where(m => m.Id < beforeId);
        }

        var rows = await query
            .OrderByDescending(m => m.Id)
            .Take(maxMessages)
            .Select(m => new ContextMessage(m.Direction, m.Username, m.Text!, m.SentAt))
            .ToListAsync(cancellationToken);

        rows.Reverse();
        return rows;
    }
}
