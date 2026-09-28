using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Bots;

/// <summary>Polls one bot (manager or role) forever until cancelled. One instance per active
/// `bots` row, run as a plain background Task by BotPollingCoordinator (not a BackgroundService —
/// BotPollingCoordinator itself is the single IHostedService; individual workers come and go at
/// runtime as bots are created/removed, which BackgroundService's fixed lifetime doesn't fit).</summary>
public class BotPollingWorker
{
    private readonly ReceivingBot _bot;
    private readonly ITelegramClient _telegramClient;
    private readonly IReadOnlyList<UpdateKind> _allowedUpdates;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PollingWorkerSettings _settings;
    private readonly PollingHealth _pollingHealth;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    public BotPollingWorker(
        ReceivingBot bot,
        ITelegramClient telegramClient,
        IReadOnlyList<UpdateKind> allowedUpdates,
        IServiceScopeFactory scopeFactory,
        PollingWorkerSettings settings,
        PollingHealth pollingHealth,
        IClock clock,
        ILogger logger)
    {
        _bot = bot;
        _telegramClient = telegramClient;
        _allowedUpdates = allowedUpdates;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _pollingHealth = pollingHealth;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await EnsureUsernameFreshWithRetryAsync(stoppingToken);

        var backoff = _settings.MinBackoff;
        var consecutiveFailures = new Dictionary<long, int>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var offset = await GetLastUpdateIdAsync(stoppingToken) + 1;
                var updates = await _telegramClient.GetUpdatesAsync(offset, _settings.LongPollTimeoutSeconds, _allowedUpdates, stoppingToken);

                foreach (var update in updates)
                {
                    await HandleWithPoisonCapAsync(update, consecutiveFailures, stoppingToken);
                }

                _pollingHealth.MarkSuccess(_clock.UtcNow);
                backoff = _settings.MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("polling loop error for bot {TelegramBotId}: {ExceptionType} {Message}", _bot.TelegramBotId, ex.GetType().Name, ex.Message);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _settings.MaxBackoff.TotalSeconds));
            }
        }
    }

    private async Task HandleWithPoisonCapAsync(
        IncomingUpdate update, Dictionary<long, int> consecutiveFailures, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();
            await handler.HandleAsync(_bot, _telegramClient, update, cancellationToken);
            consecutiveFailures.Remove(update.UpdateId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = consecutiveFailures.GetValueOrDefault(update.UpdateId) + 1;
            consecutiveFailures[update.UpdateId] = failures;

            if (failures < _settings.PoisonUpdateFailureCap)
            {
                throw;
            }

            _logger.LogError(
                "update {UpdateId} for bot {TelegramBotId} failed {FailureCount} times in a row and is being skipped: {ExceptionType}",
                update.UpdateId, _bot.TelegramBotId, failures, ex.GetType().Name);

            using var scope = _scopeFactory.CreateScope();
            var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
            await messageStore.StoreAsync(_bot.TelegramBotId, update.UpdateId, null, cancellationToken);
            consecutiveFailures.Remove(update.UpdateId);
        }
    }

    private async Task EnsureUsernameFreshWithRetryAsync(CancellationToken cancellationToken)
    {
        var backoff = _settings.MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var identity = await _telegramClient.GetMeAsync(cancellationToken);
                using var scope = _scopeFactory.CreateScope();
                var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
                await messageStore.EnsureBotStateAsync(identity, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("getMe/EnsureBotState failed for bot {TelegramBotId}: {ExceptionType}", _bot.TelegramBotId, ex.GetType().Name);

                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _settings.MaxBackoff.TotalSeconds));
            }
        }
    }

    private async Task<long> GetLastUpdateIdAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
        return await messageStore.GetLastUpdateIdAsync(_bot.TelegramBotId, cancellationToken);
    }
}
