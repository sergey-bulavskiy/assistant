using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant;
using Microsoft.EntityFrameworkCore;

static string Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
    ? Environment.GetEnvironmentVariable(name)!
    : throw new InvalidOperationException($"Missing setting: {name}");

static HashSet<long> IdSet(string name)
{
    var parts = Required(name).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new InvalidOperationException($"Invalid setting: {name}");
    var result = new HashSet<long>();
    foreach (var part in parts)
        if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) result.Add(value);
        else throw new InvalidOperationException($"Invalid setting: {name}");
    return result;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Environment.ExitCode = await ExitBoundary.RunAsync(async () =>
{
    var token = Required("TELEGRAM_BOT_TOKEN");
    var key = Required("OPENAI_API_KEY");
    var model = Required("OPENAI_MODEL");
    var connection = Required("DATABASE_CONNECTION");
    if (!Guid.TryParse(Required("SUBJECT_ID"), out var subject) || subject == Guid.Empty)
        throw new InvalidOperationException("Invalid setting: SUBJECT_ID");
    var users = IdSet("ALLOWED_USERS");
    var chats = IdSet("ALLOWED_CHATS");
    var options = new DbContextOptionsBuilder<ObservationDb>().UseNpgsql(connection).Options;
    using var telegram = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    using var openai = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    // Resolve the bot before polling. Never print its response or URL.
    using var meResponse = await telegram.PostAsJsonAsync($"https://api.telegram.org/bot{token}/getMe", new { }, stop.Token);
    meResponse.EnsureSuccessStatusCode();
    using var meBody = await JsonDocument.ParseAsync(await meResponse.Content.ReadAsStreamAsync(stop.Token), cancellationToken: stop.Token);
    if (!meBody.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Telegram getMe failed.");
    var me = meBody.RootElement.GetProperty("result");
    var botId = me.GetProperty("id").GetInt64();
    var username = me.GetProperty("username").GetString() ?? throw new InvalidOperationException("Missing bot username.");
    await using (var db = new ObservationDb(options)) await db.Database.MigrateAsync(stop.Token);
    var bot = new TelegramBot(telegram, token, botId, username, users, chats, subject,
        new ReadingExtractor(openai, key, model), new ObservationStore(options), options);
    Console.WriteLine("polling_ready");
    await TelegramPolling.RunAsync(
        (offset, cancellation) => bot.CallAsync("getUpdates",
            new { offset, timeout = 25, allowed_updates = new[] { "message" } }, cancellation),
        bot.HandleAsync, TimeSpan.FromSeconds(2), stop.Token);
}, stop.Token);
