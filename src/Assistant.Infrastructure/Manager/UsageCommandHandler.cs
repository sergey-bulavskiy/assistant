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
        var caller = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TelegramUserId == callerUserId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
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
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);

        // Own family only (spec §10.3 isolation): platform totals above never carry a family's own
        // breakdown, but the breakdown itself must never cross into another family's calls.
        var ownFamilyCalls = await _db.LlmCalls.IgnoreQueryFilters()
            .Where(c => c.FamilyId == caller.FamilyId && c.CreatedAt >= monthStart)
            .ToListAsync(cancellationToken);

        lines.Add(string.Empty);
        lines.Add($"Ваша семья (этот месяц): {ownFamilyCalls.Count} вызовов.");

        foreach (var byBot in ownFamilyCalls.GroupBy(c => c.BotId).OrderBy(g => g.Key))
        {
            lines.Add($"Бот {byBot.Key}:");
            foreach (var byModel in byBot.GroupBy(c => (c.Provider, c.Model)).OrderBy(g => g.Key.Provider).ThenBy(g => g.Key.Model))
            {
                var calls = byModel.ToList();
                var tokens = calls.Sum(c => (long)(c.InputTokens ?? 0) + (c.OutputTokens ?? 0));
                var cost = calls.Sum(c => c.Cost);
                var costText = cost == 0m ? "подписка" : $"${cost:0.0000}";
                lines.Add($"- {byModel.Key.Provider}:{byModel.Key.Model}: {calls.Count} вызовов, {tokens} токенов, {costText}.");
            }
        }

        var reply = string.Join("\n", lines);
        // Telegram message cap (spec: "keep messages under 4096 chars"). A family with enough
        // calls/models to exceed this would need pagination, out of scope for M3b -- truncate with
        // a visible marker rather than silently failing to send.
        const int telegramMessageLimit = 4096;
        if (reply.Length > telegramMessageLimit)
        {
            reply = reply[..(telegramMessageLimit - 1)] + "…";
        }

        return reply;
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
