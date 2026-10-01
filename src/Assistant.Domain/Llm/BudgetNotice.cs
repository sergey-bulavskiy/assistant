namespace Assistant.Domain.Llm;

// One row per (period kind, period start, threshold) budget notice to platform admins. Inserted
// before sending; the unique constraint makes the insert the dedup. No FamilyId: budgets are
// platform-wide, so this table is never family-scoped.
public class BudgetNotice
{
    public const string DailyPeriod = "daily";
    public const string MonthlyPeriod = "monthly";

    public long Id { get; set; }
    public string PeriodKind { get; set; } = string.Empty; // DailyPeriod | MonthlyPeriod
    public DateTimeOffset PeriodStart { get; set; }
    public int Threshold { get; set; } // the warn percent, 100 or the hard percent that was crossed
    public DateTimeOffset CreatedAt { get; set; }
}
