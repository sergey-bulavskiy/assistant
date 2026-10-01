using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Llm;

/// <summary>Starts the post-call budget-notice check (spec §10.3/review) off the reply path. The
/// request-scoped <c>AssistantDbContext</c> a <see cref="LlmGateway"/> call runs on must never be
/// touched by this check: it creates its own DI scope (its own, independent
/// <c>AssistantDbContext</c>) via <see cref="IServiceScopeFactory"/>, same as
/// <see cref="BudgetNoticeSender"/>'s own dedup-insert scope.</summary>
public interface IBudgetNoticeDispatcher
{
    /// <summary>Fires off the check and returns the (already-started) <see cref="Task"/> doing it,
    /// without having awaited it. The reply path must <b>discard</b> what this returns
    /// (<c>_ = dispatcher.Dispatch();</c>) -- it is returned only so a test can await it directly to
    /// observe completion deterministically, bounded by the check's own timeout. The returned Task
    /// never completes in a Faulted state: every exception, including the timeout firing, is caught
    /// and logged (exception type only) inside it.</summary>
    Task Dispatch();
}

public class BudgetNoticeDispatcher : IBudgetNoticeDispatcher
{
    // 5s: long enough for a real DB write + a couple of Telegram sends, short enough that it can
    // never meaningfully delay anything waiting on it (nothing on the reply path does, by design).
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BudgetNoticeDispatcher> _logger;

    public BudgetNoticeDispatcher(IServiceScopeFactory scopeFactory, ILogger<BudgetNoticeDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task Dispatch() => Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(CheckTimeout);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var budget = scope.ServiceProvider.GetRequiredService<IBudgetGuard>();
            var status = await budget.EvaluateAsync(timeoutCts.Token);
            if (status is not null)
            {
                var sender = scope.ServiceProvider.GetRequiredService<IBudgetNoticeSender>();
                await sender.NotifyAsync(status, timeoutCts.Token);
            }
        }
        catch (Exception ex)
        {
            // Never rethrown: a notice is best-effort and runs off the reply path entirely -- there
            // is nothing left for an exception here to affect. Log the exception TYPE only, same
            // rule as LlmGateway.RecordCallAsync.
            _logger.LogError("Post-call budget notice check failed: {ExceptionType}", ex.GetType().Name);
        }
    }
}
