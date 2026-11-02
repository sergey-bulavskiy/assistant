using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Assistant;

public sealed class TelegramBot(HttpClient telegram, string token, long botId, string username,
    IReadOnlySet<long> users, IReadOnlySet<long> chats, Guid subjectId,
    ReadingExtractor extractor, ObservationStore store, DbContextOptions<ObservationDb> options,
    RecipeGenerator recipeGenerator)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<JsonElement> CallAsync(string method, object payload, CancellationToken cancellationToken)
    {
        using var response = await telegram.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/{method}", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!body.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Telegram request failed.");
        return body.RootElement.GetProperty("result").Clone();
    }

    public async Task HandleAsync(JsonElement update, CancellationToken cancellationToken)
    {
        if (!update.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("from", out var sender) ||
            !sender.TryGetProperty("id", out var senderId) || !senderId.TryGetInt64(out var userId) ||
            !message.TryGetProperty("chat", out var chat) ||
            !chat.TryGetProperty("id", out var chatIdElement) || !chatIdElement.TryGetInt64(out var chatId) ||
            !users.Contains(userId) || !chats.Contains(chatId) ||
            !message.TryGetProperty("message_id", out var sourceElement) || !sourceElement.TryGetInt64(out var messageId) ||
            !message.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            return;

        var input = textElement.GetString()!;
        var split = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0) return;
        var commandParts = split[0].Split('@', 2);
        if (commandParts.Length == 2 && !string.Equals(commandParts[1], username, StringComparison.OrdinalIgnoreCase)) return;
        long? topic = message.TryGetProperty("message_thread_id", out var topicElement) && topicElement.TryGetInt64(out var topicId)
            ? topicId : null;

        if (commandParts[0] == "/prefer")
        {
            if (split.Length != 2 || string.IsNullOrWhiteSpace(split[1]) || split[1].EnumerateRunes().Count() > 500)
            {
                await ReplyAsync(chatId, messageId, topic, "Укажите предпочтение длиной до 500 символов.", cancellationToken);
                return;
            }
            var saved = await new PreferenceStore(options).SaveAsync(new FoodPreference
            {
                SubjectId = subjectId, BotId = botId, ChatId = chatId, TopicId = topic,
                MessageId = messageId, Text = split[1]
            }, cancellationToken);
            await ReplyAsync(chatId, messageId, topic,
                saved ? $"Предпочтение сохранено: {split[1]}" : "Эта команда уже обработана.", cancellationToken);
        }
        else if (commandParts[0] == "/recipe" && split.Length == 1)
        {
            var recipes = new RecipeStore(options);
            var existing = await recipes.FindSourceAsync(botId, chatId, messageId, cancellationToken);
            if (existing is not null)
            {
                await ReplyAsync(chatId, messageId, topic, RecipeFormat.Render(existing), cancellationToken);
                return;
            }
            var preference = await new PreferenceStore(options).LatestAsync(subjectId, botId, chatId, topic, cancellationToken);
            if (preference is null)
            {
                await ReplyAsync(chatId, messageId, topic, "Сначала сохраните предпочтение командой /prefer.", cancellationToken);
                return;
            }
            if (!await recipes.StartAttemptAsync(botId, chatId, messageId, cancellationToken))
            {
                await ReplyAsync(chatId, messageId, topic, "Эта попытка уже обработана. Отправьте новую команду /recipe.", cancellationToken);
                return;
            }
            RecipeDraft? draft;
            try { draft = await recipeGenerator.GenerateAsync(preference.Text, cancellationToken); }
            catch (HttpRequestException)
            {
                await ReplyAsync(chatId, messageId, topic, "Сервис временно недоступен. Отправьте новую команду /recipe.", cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ReplyAsync(chatId, messageId, topic, "Сервис временно недоступен. Отправьте новую команду /recipe.", cancellationToken);
                return;
            }
            if (draft is null)
            {
                await ReplyAsync(chatId, messageId, topic, "Не удалось составить рецепт. Отправьте новую команду /recipe.", cancellationToken);
                return;
            }
            var candidate = new SavedRecipe
            {
                SubjectId = subjectId, BotId = botId, ChatId = chatId, TopicId = topic, MessageId = messageId,
                Title = draft.Title, IngredientsJson = JsonSerializer.Serialize(draft.Ingredients),
                StepsJson = JsonSerializer.Serialize(draft.Steps)
            };
            // Reserve the largest positive bigint ID so a generated ID cannot make the sent text longer.
            candidate.Id = long.MaxValue;
            var tooLong = RecipeFormat.Render(candidate).Length > 4096 || RecipeFormat.Shopping(candidate).Length > 4096;
            candidate.Id = 0;
            if (tooLong)
            {
                await ReplyAsync(chatId, messageId, topic, "Рецепт слишком длинный. Отправьте новую команду /recipe.", cancellationToken);
                return;
            }
            var saved = await recipes.SaveAsync(candidate, cancellationToken);
            await ReplyAsync(chatId, messageId, topic, RecipeFormat.Render(saved), cancellationToken);
        }
        else if (commandParts[0] == "/shopping" && split.Length == 1)
        {
            var latest = await new RecipeStore(options).LatestAsync(subjectId, botId, chatId, topic, cancellationToken);
            await ReplyAsync(chatId, messageId, topic,
                latest is null ? "Сохранённых рецептов пока нет." : RecipeFormat.Shopping(latest), cancellationToken);
        }
        else if (commandParts[0] == "/last")
        {
            if (split.Length != 1) return;
            var last = await store.LatestAsync(subjectId, botId, chatId, topic, cancellationToken);
            await ReplyAsync(chatId, messageId, topic, last is null ? "Нет записей." : Format(last), cancellationToken);
        }
        else if (commandParts[0] == "/record" && split.Length == 2)
        {
            await using var db = new ObservationDb(options);
            var existing = await db.Observations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BotId == botId && x.ChatId == chatId && x.MessageId == messageId, cancellationToken);
            if (existing is not null)
            {
                await ReplyAsync(chatId, messageId, topic, Format(existing), cancellationToken);
                return;
            }
            Reading? reading;
            try { reading = await extractor.ExtractAsync(split[1], cancellationToken); }
            catch (HttpRequestException)
            {
                await ReplyAsync(chatId, messageId, topic, "Сервис временно недоступен.", cancellationToken);
                return;
            }
            if (reading is null)
            {
                await ReplyAsync(chatId, messageId, topic, "Не удалось распознать значение и единицу.", cancellationToken);
                return;
            }
            Console.WriteLine("extraction_completed");
            var saved = await store.SaveAsync(new Observation
            {
                SubjectId = subjectId, BotId = botId, ChatId = chatId, MessageId = messageId,
                TopicId = topic, Value = reading.Value, Unit = reading.Unit
            }, cancellationToken);
            await ReplyAsync(chatId, messageId, topic, Format(saved), cancellationToken);
        }
    }

    public static string Format(Observation row) =>
        $"Значение: {row.Value.ToString("0.############################", CultureInfo.InvariantCulture)} {row.Unit}\nИсточник: {row.BotId}/{row.ChatId}/{row.MessageId}";

    private async Task ReplyAsync(long chatId, long messageId, long? topic, string text,
        CancellationToken cancellationToken)
    {
        _ = await CallAsync("sendMessage", new
        {
            chat_id = chatId, message_thread_id = topic,
            reply_parameters = new { message_id = messageId, allow_sending_without_reply = true }, text
        }, cancellationToken);
    }
}
