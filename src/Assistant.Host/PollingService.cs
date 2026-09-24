using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Options;

namespace Assistant.Host;

public class PollingService : BackgroundService
{
    private readonly ITelegramClient _telegramClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PollingHealth _pollingHealth;
    private readonly PollingSettings _settings;
    private readonly IClock _clock;
    private readonly IOptions<BotOptions> _options;
    private readonly ILogger<PollingService> _logger;

    public PollingService(
        ITelegramClient telegramClient,
        IServiceScopeFactory scopeFactory,
        PollingHealth pollingHealth,
        PollingSettings settings,
        IClock clock,
        IOptions<BotOptions> options,
        ILogger<PollingService> logger)
    {
        _telegramClient = telegramClient;
        _scopeFactory = scopeFactory;
        _pollingHealth = pollingHealth;
        _settings = settings;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await GetMeWithRetryAsync(stoppingToken);
        if (identity is null)
        {
            return;
        }

        var ensured = await EnsureBotStateWithRetryAsync(identity, stoppingToken);
        if (!ensured)
        {
            return;
        }

        if (_options.Value.OwnerUserId is { } ownerId)
        {
            try
            {
                var buildInfo = BuildInfo.FromEnvironment(_clock);
                await _telegramClient.SendTextAsync(ownerId, null, $"🟢 Запущен {buildInfo.ShortSha}", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("failed to send startup notification: {ExceptionType}", ex.GetType().Name);
            }
        }

        var backoff = _settings.MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var offset = await GetLastUpdateIdAsync(identity.Id, stoppingToken) + 1;
                var updates = await _telegramClient.GetUpdatesAsync(offset, _settings.LongPollTimeoutSeconds, stoppingToken);
                _pollingHealth.MarkSuccess(_clock.UtcNow);
                backoff = _settings.MinBackoff;

                foreach (var update in updates)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();
                    await handler.HandleAsync(identity.Id, identity.Username, update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var redacted = SecretRedactor.Redact(ex.Message, _options.Value.Token);
                _logger.LogError("polling loop error: {ExceptionType} {Message}", ex.GetType().Name, redacted);

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

    // IMessageStore is scoped (it wraps a scoped DbContext); PollingService is a singleton hosted
    // service, so it must never hold an IMessageStore field. Instead it resolves one from a fresh
    // DI scope each time it needs the store, here and in GetLastUpdateIdAsync below.
    //
    // A transient DB failure here must not escape ExecuteAsync: BackgroundService's default
    // exception behavior is to stop the whole host, so this retries with the same exponential
    // backoff as GetMeWithRetryAsync instead of letting a single failed attempt bring the bot down.
    private async Task<bool> EnsureBotStateWithRetryAsync(BotIdentity identity, CancellationToken cancellationToken)
    {
        var backoff = _settings.MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
                await messageStore.EnsureBotStateAsync(identity, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ensureBotState failed: {ExceptionType}", ex.GetType().Name);

                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _settings.MaxBackoff.TotalSeconds));
            }
        }

        return false;
    }

    private async Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
        return await messageStore.GetLastUpdateIdAsync(botId, cancellationToken);
    }

    private async Task<BotIdentity?> GetMeWithRetryAsync(CancellationToken cancellationToken)
    {
        var backoff = _settings.MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return await _telegramClient.GetMeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("getMe failed: {ExceptionType}", ex.GetType().Name);

                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _settings.MaxBackoff.TotalSeconds));
            }
        }

        return null;
    }
}
