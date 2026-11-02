using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant;

public sealed class FoodPreference
{
    public long Id { get; set; }
    public Guid SubjectId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public long? TopicId { get; set; }
    public long MessageId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class PreferenceStore(DbContextOptions<ObservationDb> options)
{
    public async Task<bool> SaveAsync(FoodPreference preference, CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        db.FoodPreferences.Add(preference);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_food_preferences_BotId_ChatId_MessageId" })
        {
            return false;
        }
    }

    public async Task<FoodPreference?> LatestAsync(Guid subjectId, long botId, long chatId, long? topicId,
        CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        return await db.FoodPreferences.AsNoTracking()
            .Where(x => x.SubjectId == subjectId && x.BotId == botId && x.ChatId == chatId && x.TopicId == topicId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }
}
