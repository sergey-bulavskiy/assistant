using System.Collections.Concurrent;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Vet.Photos;
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
/// created role bot starts polling without a process restart, and by /settings' Disable/Enable and
/// Remove actions.</summary>
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
    private readonly IVetPhotoBackgroundLoop? _photoLoop;
    private readonly ConcurrentDictionary<long, WorkerHandle> _workers = new();
    private readonly object _workerLifecycleSync = new();
    private readonly Dictionary<long, Task> _workerStops = new();
    private bool _hostStopping;

    public BotPollingCoordinator(
        IServiceScopeFactory scopeFactory,
        ITelegramClientFactory clientFactory,
        ITokenEncryptor tokenEncryptor,
        IOptions<BotOptions> options,
        PollingWorkerSettings settings,
        PollingHealth pollingHealth,
        IClock clock,
        ILoggerFactory loggerFactory, IVetPhotoBackgroundLoop? photoLoop = null)
    {
        _scopeFactory = scopeFactory;
        _clientFactory = clientFactory;
        _tokenEncryptor = tokenEncryptor;
        _options = options;
        _settings = settings;
        _pollingHealth = pollingHealth;
        _clock = clock;
        _loggerFactory = loggerFactory;
        _photoLoop = photoLoop;
        _logger = loggerFactory.CreateLogger<BotPollingCoordinator>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_workerLifecycleSync) _hostStopping = false;
        await EnsureManagerBotRowAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var activeBots = await db.Bots.IgnoreQueryFilters()
            .Where(b => b.Status == BotStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var bot in activeBots) StartWorkerIfAbsent(bot);
    }

    /// <summary>Stops and drains all owned lifetimes. Concurrent stops share one drain task;
    /// new workers cannot register during host shutdown.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        long[] botIds;
        lock (_workerLifecycleSync)
        { _hostStopping = true; botIds = _workers.Keys.ToArray(); }
        await Task.WhenAll(botIds.Select(StopBotAsync));
    }

    public async Task StartBotAsync(long botDbId, CancellationToken cancellationToken)
    {
        lock (_workerLifecycleSync)
            if (_hostStopping || _workers.ContainsKey(botDbId)) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var bot = await db.Bots.IgnoreQueryFilters().SingleAsync(b => b.Id == botDbId, cancellationToken);
        StartWorkerIfAbsent(bot);
    }

    // The check, spawn and registration are one synchronous transition: no losing poller starts.
    private void StartWorkerIfAbsent(Bot bot)
    {
        lock (_workerLifecycleSync)
        {
            if (_hostStopping || _workers.ContainsKey(bot.Id)) return;
            if (!_workers.TryAdd(bot.Id, StartWorker(bot)))
                throw new InvalidOperationException("Bot worker registration failed.");
        }
    }

    public Task StopBotAsync(long botDbId)
    {
        lock (_workerLifecycleSync)
        {
            if (_workerStops.TryGetValue(botDbId, out var pending)) return pending;
            if (!_workers.TryGetValue(botDbId, out var handle)) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workerStops.Add(botDbId, completion.Task);
            // Cancellation callbacks/draining run outside the registration lock. The handle stays
            // registered until both tasks finish and the token source is disposed.
            _ = Task.Run(() => DrainAndReleaseAsync(botDbId, handle, completion), CancellationToken.None);
            return completion.Task;
        }
    }

    private async Task DrainAndReleaseAsync(long botDbId, WorkerHandle handle, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await StopWorkerAsync(handle); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_workerLifecycleSync)
            {
                _workers.TryRemove(botDbId, out _);
                _workerStops.Remove(botDbId);
            }
        }
        if (failure == null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }

    private static async Task StopWorkerAsync(WorkerHandle handle)
    {
        try { handle.Cts.Cancel(); }
        finally
        {
            try { await handle.RunTask; }
            finally { handle.Cts.Dispose(); }
        }
    }

    /// <summary>The manager gets messages, button taps and managed_bot events; role bots get messages,
    /// button taps (the health bot's confirmation buttons) and my_chat_member.</summary>
    public static IReadOnlyList<UpdateKind> AllowedUpdates(bool isManager) => isManager
        ? new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.CallbackQuery, UpdateKind.ManagedBot }
        : new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.CallbackQuery, UpdateKind.MyChatMember };

    private WorkerHandle StartWorker(Bot bot)
    {
        var token = bot.FamilyId is null
            ? _options.Value.ManagerToken
            : _tokenEncryptor.Decrypt(bot.TokenEncrypted ?? throw new InvalidOperationException($"bot {bot.Id} has no stored token"));

        var client = _clientFactory.Create(token);
        var receivingBot = new ReceivingBot(bot.Id, bot.TelegramBotId, bot.Username, bot.FamilyId, bot.Role);
        var allowedUpdates = AllowedUpdates(isManager: bot.FamilyId is null);

        var logger = _loggerFactory.CreateLogger<BotPollingWorker>();
        var worker = new BotPollingWorker(receivingBot, client, allowedUpdates, _scopeFactory, _settings, _pollingHealth, _clock, logger, token);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => BotRoles.IsVet(receivingBot.Role) && _photoLoop != null
            ? RunVetPairAsync(worker, receivingBot, client, cts)
            : worker.RunAsync(cts.Token), CancellationToken.None);
        return new WorkerHandle(runTask, cts);
    }

    private async Task RunVetPairAsync(BotPollingWorker worker, ReceivingBot bot, ITelegramClient client,
        CancellationTokenSource lifetime)
    {
        var polling = worker.RunAsync(lifetime.Token);
        var photos = _photoLoop!.RunAsync(bot, client, lifetime.Token);
        try { await Task.WhenAny(polling, photos); }
        finally
        {
            try { await lifetime.CancelAsync(); }
            finally { await Task.WhenAll(polling, photos); }
        }
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
