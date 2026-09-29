using Assistant.Application.Common;
using Assistant.Application.Manager;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Manager;

public class PendingBotCreations : IPendingBotCreations
{
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;

    public PendingBotCreations(AssistantDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task SetPendingRoleAsync(long creatorTelegramUserId, string role, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var expiredBefore = now - IPendingBotCreations.MaxAge;

        // Upsert in one statement so two concurrent /newbot calls from the same owner cannot both
        // try to insert; the last one wins, as with the in-memory version this replaced. Expired
        // rows (creations never confirmed in Telegram) are cleaned up on the way.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM pending_bot_creations WHERE created_at < {expiredBefore}", cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO pending_bot_creations (creator_telegram_user_id, role, created_at)
            VALUES ({creatorTelegramUserId}, {role}, {now})
            ON CONFLICT (creator_telegram_user_id) DO UPDATE SET role = EXCLUDED.role, created_at = EXCLUDED.created_at
            """,
            cancellationToken);
    }

    public async Task<string?> GetRoleAsync(long creatorTelegramUserId, CancellationToken cancellationToken)
    {
        var expiredBefore = _clock.UtcNow - IPendingBotCreations.MaxAge;
        return await _db.PendingBotCreations
            .AsNoTracking()
            .Where(p => p.CreatorTelegramUserId == creatorTelegramUserId && p.CreatedAt >= expiredBefore)
            .Select(p => p.Role)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Runs in the caller's current transaction, if any (EF Core enlists raw SQL in it).
    public async Task RemoveAsync(long creatorTelegramUserId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM pending_bot_creations WHERE creator_telegram_user_id = {creatorTelegramUserId}", cancellationToken);
}
