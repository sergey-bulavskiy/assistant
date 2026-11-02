using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Assistant;

public sealed class Observation
{
    public long Id { get; set; }
    public Guid SubjectId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public long MessageId { get; set; }
    public long? TopicId { get; set; }
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
}

public sealed class ObservationDb(DbContextOptions<ObservationDb> options) : DbContext(options)
{
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<FoodPreference> FoodPreferences => Set<FoodPreference>();
    public DbSet<SavedRecipe> SavedRecipes => Set<SavedRecipe>();
    public DbSet<RecipeAttempt> RecipeAttempts => Set<RecipeAttempt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var observation = model.Entity<Observation>();
        observation.ToTable("observations");
        observation.HasKey(x => x.Id);
        observation.Property(x => x.Unit).HasMaxLength(16).IsRequired();
        observation.Property(x => x.Value).HasColumnType("numeric");
        observation.HasIndex(x => new { x.BotId, x.ChatId, x.MessageId }).IsUnique();
        observation.HasIndex(x => new { x.SubjectId, x.BotId, x.ChatId, x.TopicId, x.Id });

        var preference = model.Entity<FoodPreference>();
        preference.ToTable("food_preferences");
        preference.HasKey(x => x.Id);
        preference.Property(x => x.Text).HasMaxLength(500).IsRequired();
        preference.HasIndex(x => new { x.BotId, x.ChatId, x.MessageId }).IsUnique();
        preference.HasIndex(x => new { x.SubjectId, x.BotId, x.ChatId, x.TopicId, x.Id });

        var recipe = model.Entity<SavedRecipe>();
        recipe.ToTable("saved_recipes");
        recipe.HasKey(x => x.Id);
        recipe.Property(x => x.Title).IsRequired();
        recipe.Property(x => x.IngredientsJson).HasColumnType("jsonb").IsRequired();
        recipe.Property(x => x.StepsJson).HasColumnType("jsonb").IsRequired();
        recipe.HasIndex(x => new { x.BotId, x.ChatId, x.MessageId }).IsUnique();
        recipe.HasIndex(x => new { x.SubjectId, x.BotId, x.ChatId, x.TopicId, x.Id });

        var attempt = model.Entity<RecipeAttempt>();
        attempt.ToTable("recipe_attempts");
        attempt.HasKey(x => x.Id);
        attempt.HasIndex(x => new { x.BotId, x.ChatId, x.MessageId }).IsUnique();
    }
}

public sealed class ObservationDbFactory : IDesignTimeDbContextFactory<ObservationDb>
{
    public ObservationDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ObservationDb>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        return new ObservationDb(options);
    }
}

public sealed class ObservationStore(DbContextOptions<ObservationDb> options)
{
    public async Task<Observation> SaveAsync(Observation observation, CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        db.Observations.Add(observation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return observation;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var fresh = new ObservationDb(options);
            return await fresh.Observations.AsNoTracking().SingleAsync(x =>
                x.BotId == observation.BotId && x.ChatId == observation.ChatId &&
                x.MessageId == observation.MessageId, cancellationToken);
        }
    }

    public async Task<Observation?> LatestAsync(Guid subjectId, long botId, long chatId, long? topicId,
        CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        return await db.Observations.AsNoTracking()
            .Where(x => x.SubjectId == subjectId && x.BotId == botId && x.ChatId == chatId && x.TopicId == topicId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }
}
