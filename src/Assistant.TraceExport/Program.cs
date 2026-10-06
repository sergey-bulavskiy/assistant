using System.Text.Json;
using Assistant.Infrastructure.Diagnostics;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (!TryParse(args, out var messageId, out var botId, out var chatId,
        out var telegramMessageId, out var outputPath))
    {
        Console.Error.WriteLine("Usage: --message-id <id> --out <absolute path>, or --bot-id <id> --chat-id <id> --telegram-message-id <id> --out <absolute path>.");
        return 2;
    }

    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Assistant");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("Trace export database connection is not configured.");
        return 2;
    }

    try
    {
        var builder = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(builder, connectionString);
        await using var db = new AssistantDbContext(builder.Options);
        var knownKeys = new[] { "TELEGRAM_MANAGER_BOT_TOKEN", "TOKEN_ENCRYPTION_KEY",
            "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "OPENAI_API_KEY",
            "ANTHROPIC_PROXY", "OPENAI_PROXY" };
        var knownSecrets = knownKeys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        knownSecrets["ConnectionStrings:Assistant"] = connectionString;
        var registry = new TraceSecretRegistry(new ConfigurationBuilder()
            .AddInMemoryCollection(knownSecrets).Build());
        var service = new TraceExportService(db, registry);
        var document = messageId is { } id
            ? await service.ReadByMessageIdAsync(id, CancellationToken.None)
            : await service.ReadByTelegramReferenceAsync(botId!.Value, chatId!.Value,
                telegramMessageId!.Value, CancellationToken.None);
        if (document is null)
        {
            Console.Error.WriteLine("Trace not found for the selected source.");
            return 1;
        }

        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(outputPath!, fileOptions))
        {
            await JsonSerializer.SerializeAsync(stream, document,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = true });
        }
        Console.WriteLine($"Exported {document.Traces.Count} trace(s) to {outputPath}.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Trace export failed: {ex.GetType().Name}.");
        return 1;
    }
}

static bool TryParse(string[] args, out long? messageId, out long? botId,
    out long? chatId, out int? telegramMessageId, out string? outputPath)
{
    messageId = botId = chatId = null;
    telegramMessageId = null;
    outputPath = null;
    if (args.Length is not (4 or 8) || args.Length % 2 != 0) return false;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (!values.TryAdd(args[i], args[i + 1])) return false;
    }
    if (!values.TryGetValue("--out", out var output)
        || !TraceExportPath.TryResolve(output, out outputPath)) return false;
    if (values.Count == 2 && values.TryGetValue("--message-id", out var rawId)
        && long.TryParse(rawId, out var id) && id > 0)
    {
        messageId = id;
        return true;
    }
    if (values.Count == 4 && values.TryGetValue("--bot-id", out var rawBot)
        && values.TryGetValue("--chat-id", out var rawChat)
        && values.TryGetValue("--telegram-message-id", out var rawMessage)
        && long.TryParse(rawBot, out var bot) && long.TryParse(rawChat, out var chat)
        && int.TryParse(rawMessage, out var message) && message > 0)
    {
        botId = bot;
        chatId = chat;
        telegramMessageId = message;
        return true;
    }
    return false;
}
