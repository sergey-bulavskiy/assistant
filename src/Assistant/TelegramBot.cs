using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Assistant;

public sealed class TelegramBot(HttpClient telegram, string token, long botId, string username,
    IReadOnlySet<long> users, IReadOnlySet<long> chats, Guid subjectId,
    ReadingExtractor extractor, ObservationStore store, DbContextOptions<ObservationDb> options)
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

        if (commandParts[0] == "/last")
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
            reply_parameters = new { message_id = messageId }, text
        }, cancellationToken);
    }
}
