using Assistant.Application.Common;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Llm;

/// <summary>Ordered by severity: the numeric order is what "most severe of day/month" compares.</summary>
public enum BudgetState { Normal, Warn, Soft, Hard }

/// <summary>One budget period's spend against its limit. <see cref="Percent"/> is rounded, for
/// display only; <see cref="State"/> is computed from the exact ratio.</summary>
public record BudgetPeriodStatus(
    string Kind, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, decimal Spend, decimal Limit, decimal HardCap, int Percent, BudgetState State);

public record BudgetStatus(BudgetPeriodStatus Daily, BudgetPeriodStatus Monthly)
{
    public BudgetState Overall => (BudgetState)Math.Max((int)Daily.State, (int)Monthly.State);

    /// <summary>The period whose reset lifts the current restriction: the monthly one whenever it
    /// is (at least) as severe as the overall state -- a new day would not help then -- otherwise
    /// the daily one.</summary>
    public BudgetPeriodStatus Binding => Monthly.State >= Overall ? Monthly : Daily;
}

public interface IBudgetGuard
{
    /// <summary>Null when no budget is configured (no paid entry can exist then): callers never
    /// restrict and never estimate.</summary>
    Task<BudgetStatus?> EvaluateAsync(CancellationToken cancellationToken);
}

public class BudgetGuard : IBudgetGuard
{
    private readonly BudgetConfig? _config;
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;

    public BudgetGuard(LlmConfig config, AssistantDbContext db, IClock clock)
    {
        _config = config.Budget;
        _db = db;
        _clock = clock;
    }

    public async Task<BudgetStatus?> EvaluateAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            return null;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        // Budgets are platform-wide (one money budget across every family), so spend is summed over
        // ALL families' llm_calls rows. IgnoreQueryFilters is deliberate here: this is the one
        // platform-wide spend query; every other llm_calls query stays family-scoped.
        var monthlyRows = _db.LlmCalls.IgnoreQueryFilters().Where(c => c.CreatedAt >= monthStart);
        var monthlySpend = await monthlyRows.SumAsync(c => c.Cost, cancellationToken);
        var dailySpend = await monthlyRows.Where(c => c.CreatedAt >= dayStart).SumAsync(c => c.Cost, cancellationToken);

        return new BudgetStatus(
            EvaluatePeriod(BudgetNotice.DailyPeriod, dayStart, dayStart.AddDays(1), dailySpend, _config.DailyUsd, _config),
            EvaluatePeriod(BudgetNotice.MonthlyPeriod, monthStart, monthStart.AddMonths(1), monthlySpend, _config.MonthlyUsd, _config));
    }

    /// <summary>State from the exact spend/limit ratio, first match wins: at or above hard% is Hard,
    /// at or above 100% is Soft, at or above warn% is Warn, otherwise Normal.</summary>
    public static BudgetPeriodStatus EvaluatePeriod(
        string kind, DateTimeOffset periodStart, DateTimeOffset periodEnd, decimal spend, decimal limit, BudgetConfig config)
    {
        var ratio = limit == 0 ? 0m : spend / limit;
        var state = ratio >= config.HardPercent / 100m ? BudgetState.Hard
            : ratio >= 1m ? BudgetState.Soft
            : ratio >= config.WarnPercent / 100m ? BudgetState.Warn
            : BudgetState.Normal;
        var percent = (int)Math.Round(ratio * 100m, MidpointRounding.AwayFromZero);
        var hardCap = limit * config.HardPercent / 100m;
        return new BudgetPeriodStatus(kind, periodStart, periodEnd, spend, limit, hardCap, percent, state);
    }
}
