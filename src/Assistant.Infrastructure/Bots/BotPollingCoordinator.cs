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
    private readonly ILogger<BotPollingCoordinator> _logger;
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
        _logger = loggerFactory.CreateLogger<BotPollingCoordinator>();
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
            var handle = StartWorker(bot);
            if (!_workers.TryAdd(bot.Id, handle))
            {
                handle.Cts.Cancel();
                await handle.RunTask;
                handle.Cts.Dispose();
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _workers.Values)
        {
            handle.Cts.Cancel();
        }

        await Task.WhenAll(_workers.Values.Select(h => h.RunTask));

        foreach (var handle in _workers.Values)
        {
            handle.Cts.Dispose();
        }
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
        var handle = StartWorker(bot);

        if (!_workers.TryAdd(bot.Id, handle))
        {
            // Another concurrent StartBotAsync call already registered a worker for this bot id first;
            // cancel and drain the losing worker so it never runs orphaned.
            handle.Cts.Cancel();
            await handle.RunTask;
            handle.Cts.Dispose();
        }
    }

    /// <summary>Stops polling a bot (e.g. /settings' Remove action). No-op if it isn't running.</summary>
    public async Task StopBotAsync(long botDbId)
    {
        if (_workers.TryRemove(botDbId, out var handle))
        {
            handle.Cts.Cancel();
            await handle.RunTask;
            handle.Cts.Dispose();
        }
    }

    private WorkerHandle StartWorker(Bot bot)
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
        var worker = new BotPollingWorker(receivingBot, client, allowedUpdates, _scopeFactory, _settings, _pollingHealth, _clock, logger, token);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => worker.RunAsync(cts.Token), CancellationToken.None);
        return new WorkerHandle(runTask, cts);
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
        var identity = await GetMeWithRetryAsync(client, cancellationToken);

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

    // Unlike a BotPollingWorker's own retry loop (which runs as a detached background Task and so
    // never blocks startup), this call is awaited directly inside IHostedService.StartAsync — an
    // unguarded call here would fail the whole host on a single transient Telegram API hiccup at
    // boot. Retries with the same backoff schedule as polling, but bounded, so a genuinely
    // unreachable Telegram API still fails startup rather than hanging it forever.
    private async Task<BotIdentity> GetMeWithRetryAsync(ITelegramClient client, CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;
        var backoff = _settings.MinBackoff;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await client.GetMeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "manager getMe failed (attempt {Attempt}/{MaxAttempts}): {ExceptionType}", attempt, maxAttempts, ex.GetType().Name);
                await Task.Delay(backoff, cancellationToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _settings.MaxBackoff.TotalSeconds));
            }
        }

        return await client.GetMeAsync(cancellationToken);
    }
}
