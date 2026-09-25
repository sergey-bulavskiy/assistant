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
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<PollingService> _logger;

    public PollingService(
        ITelegramClient telegramClient,
        IServiceScopeFactory scopeFactory,
        PollingHealth pollingHealth,
        PollingSettings settings,
        IClock clock,
        IOptions<BotOptions> options,
        BuildInfo buildInfo,
        ILogger<PollingService> logger)
    {
        _telegramClient = telegramClient;
        _scopeFactory = scopeFactory;
        _pollingHealth = pollingHealth;
        _settings = settings;
        _clock = clock;
        _options = options;
        _buildInfo = buildInfo;
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
                // M9: use the BuildInfo singleton registered in DI (computed once, at process
                // startup) instead of recomputing one from the environment here.
                await _telegramClient.SendTextAsync(ownerId, null, $"🟢 Запущен {_buildInfo.ShortSha}", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("failed to send startup notification: {ExceptionType}", ex.GetType().Name);
            }
        }

        var backoff = _settings.MinBackoff;

        // I2: consecutive-failure count per update_id, kept outside the loop body so it survives
        // across retries of the same update on later iterations (a failed update is never removed
        // from the batch — the next GetUpdatesAsync call re-fetches it via the unmoved offset).
        var consecutiveFailures = new Dictionary<long, int>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var offset = await GetLastUpdateIdAsync(identity.Id, stoppingToken) + 1;
                var updates = await _telegramClient.GetUpdatesAsync(offset, _settings.LongPollTimeoutSeconds, stoppingToken);

                foreach (var update in updates)
                {
                    await HandleWithPoisonCapAsync(identity, update, consecutiveFailures, stoppingToken);
                }

                // I2(a): only mark success and reset backoff once the *whole* batch has been
                // handled. Doing this right after GetUpdatesAsync (before handling) made a
                // permanently throwing HandleAsync retry at the minimum backoff forever while
                // /health kept reporting healthy — GetUpdatesAsync itself was never what failed.
                _pollingHealth.MarkSuccess(_clock.UtcNow);
                backoff = _settings.MinBackoff;
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

    // I2(b)/(c): a failing update rethrows so the batch is abandoned and the outer catch applies
    // the normal exponential backoff before the next attempt re-fetches it (same offset, since the
    // failure means state.LastUpdateId never advanced past it) — unless this is the update's
    // PoisonUpdateFailureCap-th consecutive failure, in which case it is given up on: its offset is
    // recorded with no message (StoreAsync's offset-only path) so it is never re-fetched, and
    // processing continues with the rest of the batch instead of retrying this update forever.
    private async Task HandleWithPoisonCapAsync(
        BotIdentity identity,
        IncomingUpdate update,
        Dictionary<long, int> consecutiveFailures,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();
            await handler.HandleAsync(identity.Id, identity.Username, update, cancellationToken);
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

            // Never log message text or the bot token here — only the update id, the failure
            // count and the exception's type name.
            _logger.LogError(
                "update {UpdateId} failed {FailureCount} times in a row and is being skipped: {ExceptionType}",
                update.UpdateId,
                failures,
                ex.GetType().Name);

            using var scope = _scopeFactory.CreateScope();
            var messageStore = scope.ServiceProvider.GetRequiredService<IMessageStore>();
            await messageStore.StoreAsync(identity.Id, update.UpdateId, null, cancellationToken);
            consecutiveFailures.Remove(update.UpdateId);
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
