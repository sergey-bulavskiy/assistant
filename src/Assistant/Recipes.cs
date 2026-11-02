using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant;

public sealed record RecipeIngredient(string Name, decimal Grams);
public sealed record RecipeDraft(string Title, IReadOnlyList<RecipeIngredient> Ingredients, IReadOnlyList<string> Steps);

public sealed class RecipeAttempt
{
    public long Id { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public long MessageId { get; set; }
}

public sealed class SavedRecipe
{
    public long Id { get; set; }
    public Guid SubjectId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public long? TopicId { get; set; }
    public long MessageId { get; set; }
    public string Title { get; set; } = "";
    public string IngredientsJson { get; set; } = "";
    public string StepsJson { get; set; } = "";
}

public sealed class RecipeStore(DbContextOptions<ObservationDb> options)
{
    public async Task<bool> StartAttemptAsync(long botId, long chatId, long messageId, CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        db.RecipeAttempts.Add(new RecipeAttempt { BotId = botId, ChatId = chatId, MessageId = messageId });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_recipe_attempts_BotId_ChatId_MessageId" })
        {
            return false;
        }
    }

    public async Task<SavedRecipe?> FindSourceAsync(long botId, long chatId, long messageId, CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        return await db.SavedRecipes.AsNoTracking().SingleOrDefaultAsync(x => x.BotId == botId &&
            x.ChatId == chatId && x.MessageId == messageId, cancellationToken);
    }

    public async Task<SavedRecipe> SaveAsync(SavedRecipe recipe, CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        db.SavedRecipes.Add(recipe);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return recipe;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_saved_recipes_BotId_ChatId_MessageId" })
        {
            return (await FindSourceAsync(recipe.BotId, recipe.ChatId, recipe.MessageId, cancellationToken))!;
        }
    }

    public async Task<SavedRecipe?> LatestAsync(Guid subjectId, long botId, long chatId, long? topicId,
        CancellationToken cancellationToken)
    {
        await using var db = new ObservationDb(options);
        return await db.SavedRecipes.AsNoTracking().Where(x => x.SubjectId == subjectId && x.BotId == botId &&
            x.ChatId == chatId && x.TopicId == topicId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }
}

public static class RecipeFormat
{
    public static string Shopping(SavedRecipe recipe)
    {
        var ingredients = JsonSerializer.Deserialize<RecipeIngredient[]>(recipe.IngredientsJson)!;
        var lines = ingredients.Select(i => $"- {i.Name}: {i.Grams.ToString("0.############################", CultureInfo.InvariantCulture)} г");
        return $"Список покупок для рецепта #{recipe.Id}\nИсточник: {recipe.BotId}/{recipe.ChatId}/{recipe.MessageId}\n" +
               string.Join("\n", lines);
    }

    public static string Render(SavedRecipe recipe)
    {
        var ingredients = JsonSerializer.Deserialize<RecipeIngredient[]>(recipe.IngredientsJson)!;
        var steps = JsonSerializer.Deserialize<string[]>(recipe.StepsJson)!;
        var grams = ingredients.Select(i => $"- {i.Name}: {i.Grams.ToString("0.############################", CultureInfo.InvariantCulture)} г");
        return $"Рецепт #{recipe.Id}\nИсточник: {recipe.BotId}/{recipe.ChatId}/{recipe.MessageId}\n{recipe.Title}\n" +
               "Ингредиенты:\n" + string.Join("\n", grams) + "\nПриготовление:\n" + string.Join("\n", steps);
    }
}
