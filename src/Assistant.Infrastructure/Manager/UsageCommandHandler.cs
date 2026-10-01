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

    public UsageCommandHandler(AssistantDbContext db, IBudgetGuard budgetGuard, IClock clock)
    {
        _db = db;
        _budgetGuard = budgetGuard;
        _clock = clock;
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
    /// own breakdown, but the breakdown itself must never cross into another family's calls. Grouping
    /// and the token/cost sums are done in SQL (GroupBy + Sum, translated by EF), not by loading every
    /// row and aggregating in memory.</summary>
    private async Task AppendFamilyPeriodAsync(List<string> lines, string label, long familyId, DateTimeOffset periodStart, CancellationToken cancellationToken)
    {
        var totalCalls = await _db.LlmCalls.IgnoreQueryFilters()
            .CountAsync(c => c.FamilyId == familyId && c.CreatedAt >= periodStart, cancellationToken);
        lines.Add($"{label}: {totalCalls} вызовов.");

        var groups = await (
            from c in _db.LlmCalls.IgnoreQueryFilters()
            join b in _db.Bots.IgnoreQueryFilters() on c.BotId equals b.Id
            where c.FamilyId == familyId && c.CreatedAt >= periodStart
            group c by new { c.BotId, b.Username, c.Provider, c.Model } into g
            select new
            {
                g.Key.BotId,
                g.Key.Username,
                g.Key.Provider,
                g.Key.Model,
                Calls = g.Count(),
                InputTokens = g.Sum(x => (long)(x.InputTokens ?? 0)),
                OutputTokens = g.Sum(x => (long)(x.OutputTokens ?? 0)),
                Cost = g.Sum(x => x.Cost)
            }).ToListAsync(cancellationToken);

        foreach (var byBot in groups.GroupBy(g => (g.BotId, g.Username)).OrderBy(g => g.Key.BotId))
        {
            lines.Add($"Бот {byBot.Key.Username}:");
            foreach (var row in byBot.OrderBy(r => r.Provider).ThenBy(r => r.Model))
            {
                var tokens = row.InputTokens + row.OutputTokens;
                var costText = row.Cost == 0m ? "подписка" : $"${row.Cost:0.0000}";
                lines.Add($"- {row.Provider}:{row.Model}: {row.Calls} вызовов, {tokens} токенов, {costText}.");
            }
        }
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
