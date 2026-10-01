using Assistant.Application.Common;
using Assistant.Domain.Families;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Manager;

/// <summary>/usage, owner-only (spec §5/§10.3). Kept as its own class, invoked from
/// ManagerUpdateHandler, rather than grown inside it (M15, matching the existing file-size
/// concern). Platform totals + state are shown to any owner; per-bot/per-model breakdown is shown
/// only for the caller's own family, for the current calendar month (spec §5: "today and this
/// month").
///
/// Deviation from the plan's Step 1 code: the constructor drops the separate `BudgetConfig? budget`
/// parameter. IBudgetGuard.EvaluateAsync already returns null for "no budget configured" (whether
/// that's because LlmConfig.Budget is null or, per execution notes, because LLM itself is off and
/// NullBudgetGuard is registered) -- a second, redundant config check here would just be another
/// place that distinction could drift out of sync with the guard's own.</summary>
public class UsageCommandHandler
{
    private readonly AssistantDbContext _db;
    private readonly IBudgetGuard _budgetGuard;
    private readonly IClock _clock;
    private readonly LlmConfig? _llmConfig;

    public UsageCommandHandler(AssistantDbContext db, IBudgetGuard budgetGuard, IClock clock, LlmConfig? llmConfig)
    {
        _db = db;
        _budgetGuard = budgetGuard;
        _clock = clock;
        _llmConfig = llmConfig;
    }

    public async Task<string> BuildReplyAsync(long callerUserId, CancellationToken cancellationToken)
    {
        // Deterministic even if the caller happens to own more than one family (not possible today --
        // /claim refuses a second family outright -- but FirstOrDefaultAsync with no ordering would
        // otherwise be a database-order-dependent pick).
        var caller = await _db.FamilyMembers.IgnoreQueryFilters()
            .Where(m => m.TelegramUserId == callerUserId && m.IsOwner && m.Status == FamilyMemberStatus.Approved)
            .OrderBy(m => m.FamilyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (caller is null)
        {
            return "У вас нет прав.";
        }

        var lines = new List<string>();
        var status = await _budgetGuard.EvaluateAsync(cancellationToken);
        if (status is null)
        {
            // Budgets aren't configured -- still show call counts (spec: "when budgets aren't
            // configured, say so and still show the call counts"), so this falls through to the
            // own-family breakdown below rather than returning early.
            lines.Add("Бюджет не настроен.");
        }
        else
        {
            lines.Add($"Сегодня: ${status.Daily.Spend:0.00} из ${status.Daily.Limit:0.00} ({status.Daily.Percent}%, {StateText(status.Daily.State)}).");
            lines.Add($"Этот месяц: ${status.Monthly.Spend:0.00} из ${status.Monthly.Limit:0.00} ({status.Monthly.Percent}%, {StateText(status.Monthly.State)}).");
        }

        var now = _clock.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);

        // Platform-wide call counts (review nit): unlike the spend/state lines above (already
        // platform-wide), /usage had no platform-wide ACTIVITY figure at all -- every owner can see
        // how busy the whole platform is, not just their own family's calls below.
        var platformCallsToday = await _db.LlmCalls.IgnoreQueryFilters().CountAsync(c => c.CreatedAt >= dayStart, cancellationToken);
        var platformCallsThisMonth = await _db.LlmCalls.IgnoreQueryFilters().CountAsync(c => c.CreatedAt >= monthStart, cancellationToken);
        lines.Add($"Вызовов на платформе: {platformCallsToday} сегодня, {platformCallsThisMonth} в этом месяце.");

        lines.Add(string.Empty);
        await AppendFamilyPeriodAsync(lines, "Ваша семья (сегодня)", caller.FamilyId, dayStart, cancellationToken);
        lines.Add(string.Empty);
        await AppendFamilyPeriodAsync(lines, "Ваша семья (этот месяц)", caller.FamilyId, monthStart, cancellationToken);

        var reply = string.Join("\n", lines);
        // Telegram message cap (spec: "keep messages under 4096 chars"). A family with enough
        // calls/models to exceed this would need pagination, out of scope for M3b -- truncate at the
        // last full line within the cap rather than mid-line, with a visible marker.
        const int telegramMessageLimit = 4096;
        if (reply.Length > telegramMessageLimit)
        {
            var cut = reply.LastIndexOf('\n', telegramMessageLimit - 2);
            reply = (cut > 0 ? reply[..cut] : reply[..(telegramMessageLimit - 1)]) + "\n…";
        }

        return reply;
    }

    /// <summary>Own family only (spec §10.3 isolation): platform totals above never carry a family's
    /// own breakdown, but the breakdown itself must never cross into another family's calls. The
    /// per-call rows are fetched with SQL doing the filtering (family + period, translated by EF);
    /// the bot-bucketing (deleted bots collapse into one group) and per-model aggregation run over
    /// that already-small, already-scoped result set in memory, since a single family's own calls
    /// for one day/month are never large enough for this to matter.
    ///
    /// A LEFT join against bots (review fix): the previous inner join silently dropped every row whose
    /// bot had since been deleted from the whole breakdown (while the total call count above still
    /// counted them) -- `b.Username` is NOT NULL at the DB level, so a left-join miss is exactly
    /// `Username == null` here, with no separate "does the bot still exist" flag needed.</summary>
    private async Task AppendFamilyPeriodAsync(List<string> lines, string label, long familyId, DateTimeOffset periodStart, CancellationToken cancellationToken)
    {
        var totalCalls = await _db.LlmCalls.IgnoreQueryFilters()
            .CountAsync(c => c.FamilyId == familyId && c.CreatedAt >= periodStart, cancellationToken);
        lines.Add($"{label}: {totalCalls} вызовов.");

        var rows = await (
            from c in _db.LlmCalls.IgnoreQueryFilters()
            join b in _db.Bots.IgnoreQueryFilters() on c.BotId equals b.Id into botJoin
            from b in botJoin.DefaultIfEmpty()
            where c.FamilyId == familyId && c.CreatedAt >= periodStart
            select new
            {
                c.BotId,
                Username = (string?)b.Username,
                Role = (string?)b.Role,
                c.Provider,
                c.Model,
                c.InputTokens,
                c.OutputTokens,
                c.Cost
            }).ToListAsync(cancellationToken);

        // A deleted bot's rows collapse into one "удалённый бот" bucket regardless of the original
        // BotId -- that id is no longer meaningful once the bot itself is gone. A live bot keeps its
        // own per-BotId group even if (defensively) its username/role were ever blank. Null key =
        // the deleted-bot bucket; sorted after every live bot, which are ordered by BotId.
        var byBot = rows
            .GroupBy(r => r.Username is null ? (long?)null : r.BotId)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key ?? long.MaxValue);

        var fallbackOrdinal = 0;
        foreach (var botGroup in byBot)
        {
            var first = botGroup.First();
            var isDeleted = first.Username is null;
            string botLabel;
            if (isDeleted)
            {
                botLabel = "удалённый бот";
            }
            else if (!string.IsNullOrEmpty(first.Username))
            {
                botLabel = first.Username!;
            }
            else if (!string.IsNullOrEmpty(first.Role))
            {
                botLabel = first.Role!;
            }
            else
            {
                fallbackOrdinal++;
                botLabel = $"#{fallbackOrdinal}";
            }

            lines.Add($"Бот {botLabel}:");
            foreach (var row in botGroup
                .GroupBy(r => new { r.Provider, r.Model })
                .Select(g => new
                {
                    g.Key.Provider,
                    g.Key.Model,
                    Calls = g.Count(),
                    InputTokens = g.Sum(x => (long)(x.InputTokens ?? 0)),
                    OutputTokens = g.Sum(x => (long)(x.OutputTokens ?? 0)),
                    Cost = g.Sum(x => x.Cost)
                })
                .OrderBy(r => r.Provider).ThenBy(r => r.Model))
            {
                var tokens = row.InputTokens + row.OutputTokens;
                var costText = IsZeroPriceModel(row.Provider, row.Model, row.Cost) ? "подписка" : $"${row.Cost:0.0000}";
                lines.Add($"- {row.Provider}:{row.Model}: {row.Calls} вызовов, {tokens} токенов, {costText}.");
            }
        }
    }

    /// <summary>Review fix: a "подписка" label used to mean "this row's recorded Cost happens to be
    /// 0" -- which a genuinely paid model also hits whenever a call failed before any usage was
    /// billed (e.g. LlmGateway records a `LimitReached` attempt at cost 0 regardless of the model's
    /// real price), mislabelling an expensive provider's failed attempt as free. The catalog's own
    /// price list is what actually decides free vs. paid for a model that is still configured today.
    /// `claude-cli:` is always free regardless of catalog state (it never has a price entry either
    /// way). Falls back to the row's own recorded cost (the old behaviour) only when the model isn't
    /// in the current catalog at all -- LLM off, or the entry was since removed/repriced -- since
    /// there is nothing authoritative left to check.</summary>
    private bool IsZeroPriceModel(string provider, string model, decimal rowCost)
    {
        if (string.Equals(provider, "claude-cli", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_llmConfig is not null && _llmConfig.Prices.TryGetValue(model, out var configured))
        {
            return LlmCostCalculator.IsZero(configured);
        }

        return rowCost == 0m;
    }

    private static string StateText(BudgetState state) => state switch
    {
        BudgetState.Normal => "норма",
        BudgetState.Warn => "предупреждение",
        BudgetState.Soft => "мягкое ограничение",
        BudgetState.Hard => "жёсткое ограничение",
        _ => "норма"
    };
}
