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
                await StopWorkerAsync(handle);
            }
        }
    }

    /// <summary>Stops every currently tracked worker and clears `_workers`, so a repeat call (the
    /// hosting layer does not guarantee IHostedService.StopAsync is invoked exactly once — ASP.NET
    /// Core's test host in particular can call it more than once during teardown) finds nothing left
    /// to stop and is a no-op rather than re-cancelling/re-disposing an already-stopped worker's
    /// CancellationTokenSource.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Snapshot-and-remove each entry (rather than iterating _workers.Values directly) so a
        // concurrent StopBotAsync/StartBotAsync racing for the same bot id can never observe or
        // touch the same handle we're stopping here.
        var botIds = _workers.Keys.ToList();
        var handles = new List<WorkerHandle>();
        foreach (var botId in botIds)
        {
            if (_workers.TryRemove(botId, out var handle))
            {
                handles.Add(handle);
            }
        }

        foreach (var handle in handles)
        {
            CancelIgnoringDisposed(handle);
        }

        await Task.WhenAll(handles.Select(h => h.RunTask));

        foreach (var handle in handles)
        {
            DisposeIgnoringDisposed(handle);
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
            await StopWorkerAsync(handle);
        }
    }

    /// <summary>Stops polling a bot (e.g. /settings' Remove action). No-op if it isn't running.</summary>
    public async Task StopBotAsync(long botDbId)
    {
        // TryRemove is atomic against a concurrent StopAsync (also a remove-then-stop), so the two
        // can never both grab the same handle and double-cancel/double-dispose its Cts.
        if (_workers.TryRemove(botDbId, out var handle))
        {
            await StopWorkerAsync(handle);
        }
    }

    /// <summary>Cancels a worker's token, awaits its run task to finish draining, then disposes the
    /// token source — tolerating the Cts already being disposed (e.g. by a racing/duplicate stop of
    /// the same handle elsewhere), since Cancel()/Dispose() both throw ObjectDisposedException in
    /// that case and stopping a worker must stay idempotent.</summary>
    private static async Task StopWorkerAsync(WorkerHandle handle)
    {
        CancelIgnoringDisposed(handle);
        await handle.RunTask;
        DisposeIgnoringDisposed(handle);
    }

    private static void CancelIgnoringDisposed(WorkerHandle handle)
    {
        try
        {
            handle.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped by a racing or duplicate call; nothing left to cancel.
        }
    }

    private static void DisposeIgnoringDisposed(WorkerHandle handle)
    {
        try
        {
            handle.Cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a racing or duplicate call.
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
