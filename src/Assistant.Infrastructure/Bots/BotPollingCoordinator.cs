using System.Collections.Concurrent;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Infrastructure.Bots;

/// <summary>Starts one BotPollingWorker per active `bots` row at boot (manager first, ensuring its
/// row exists), and can start/stop a worker for one bot at runtime — used by /newbot so a freshly
/// created role bot starts polling without a process restart, and by /settings' Remove action to
/// stop polling a removed bot (both wired in later tasks).</summary>
public class BotPollingCoordinator : IHostedService
{
    private sealed record WorkerHandle(Task RunTask, CancellationTokenSource Cts);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramClientFactory _clientFactory;
    private readonly ITokenEncryptor _tokenEncryptor;
    private readonly IOptions<BotOptions> _options;
    private readonly PollingWorkerSettings _settings;
    private readonly PollingHealth _pollingHealth;
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<long, WorkerHandle> _workers = new();

    public BotPollingCoordinator(
        IServiceScopeFactory scopeFactory,
        ITelegramClientFactory clientFactory,
        ITokenEncryptor tokenEncryptor,
        IOptions<BotOptions> options,
        PollingWorkerSettings settings,
        PollingHealth pollingHealth,
        IClock clock,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _clientFactory = clientFactory;
        _tokenEncryptor = tokenEncryptor;
        _options = options;
        _settings = settings;
        _pollingHealth = pollingHealth;
        _clock = clock;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureManagerBotRowAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var activeBots = await db.Bots.IgnoreQueryFilters()
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var bot in activeBots)
        {
            StartWorker(bot);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _workers.Values)
        {
            handle.Cts.Cancel();
        }

        return Task.WhenAll(_workers.Values.Select(h => h.RunTask));
    }

    /// <summary>Starts polling a bot that was just created (e.g. by /newbot) without restarting the
    /// process. No-op if the bot is already being polled.</summary>
    public async Task StartBotAsync(long botDbId, CancellationToken cancellationToken)
    {
        if (_workers.ContainsKey(botDbId))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var bot = await db.Bots.IgnoreQueryFilters().SingleAsync(b => b.Id == botDbId, cancellationToken);
        StartWorker(bot);
    }

    /// <summary>Stops polling a bot (e.g. /settings' Remove action). No-op if it isn't running.</summary>
    public async Task StopBotAsync(long botDbId)
    {
        if (_workers.TryRemove(botDbId, out var handle))
        {
            handle.Cts.Cancel();
            await handle.RunTask;
        }
    }

    private void StartWorker(Bot bot)
    {
        var token = bot.FamilyId is null
            ? _options.Value.ManagerToken
            : _tokenEncryptor.Decrypt(bot.TokenEncrypted ?? throw new InvalidOperationException($"bot {bot.Id} has no stored token"));

        var client = _clientFactory.Create(token);
        var receivingBot = new ReceivingBot(bot.Id, bot.TelegramBotId, bot.Username, bot.FamilyId, bot.Role);
        var allowedUpdates = bot.FamilyId is null
            ? new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.CallbackQuery, UpdateKind.ManagedBot }
            : new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.MyChatMember };

        var logger = _loggerFactory.CreateLogger<BotPollingWorker>();
        var worker = new BotPollingWorker(receivingBot, client, allowedUpdates, _scopeFactory, _settings, _pollingHealth, _clock, logger);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => worker.RunAsync(cts.Token), CancellationToken.None);
        _workers[bot.Id] = new WorkerHandle(runTask, cts);
    }

    private async Task EnsureManagerBotRowAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();

        var managerBot = await db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Role == "manager", cancellationToken);
        if (managerBot is not null)
        {
            return;
        }

        var client = _clientFactory.Create(_options.Value.ManagerToken);
        var identity = await client.GetMeAsync(cancellationToken);

        managerBot = new Bot
        {
            FamilyId = null,
            TelegramBotId = identity.Id,
            Username = identity.Username,
            Role = "manager",
            TokenEncrypted = null,
            Status = BotStatus.Active,
            LastUpdateId = 0,
            CreatedAt = _clock.UtcNow
        };
        db.Bots.Add(managerBot);
        await db.SaveChangesAsync(cancellationToken);
    }
}
