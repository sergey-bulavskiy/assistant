using System.Globalization;
using Assistant.Application.Common;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class BudgetGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static BudgetPeriodStatus Evaluate(string kind, decimal spend, decimal limit = 100m, int warnPercent = 80, int hardPercent = 120) =>
        BudgetGuard.EvaluatePeriod(kind, Start, Start.AddDays(1), spend, limit, new BudgetConfig(limit, limit, warnPercent, hardPercent));

    [Theory]
    [InlineData("0", BudgetState.Normal)]
    [InlineData("79.9999", BudgetState.Normal)]
    [InlineData("80", BudgetState.Warn)]      // exactly warn% -> Warn
    [InlineData("99.9999", BudgetState.Warn)]
    [InlineData("100", BudgetState.Soft)]     // exactly 100% -> Soft
    [InlineData("119.9999", BudgetState.Soft)]
    [InlineData("120", BudgetState.Hard)]     // exactly hard% -> Hard
    [InlineData("150", BudgetState.Hard)]
    public void State_edges_are_exact(string spend, BudgetState expected)
    {
        Evaluate("daily", decimal.Parse(spend, CultureInfo.InvariantCulture)).State.ShouldBe(expected);
    }

    [Fact]
    public void Edges_follow_the_configured_percentages_and_limit()
    {
        // limit $10, warn 50%, hard 150%: $4.99 normal, $5 warn, $10 soft, $14.99 soft, $15 hard.
        Evaluate("daily", 4.99m, limit: 10m, warnPercent: 50, hardPercent: 150).State.ShouldBe(BudgetState.Normal);
        Evaluate("daily", 5m, limit: 10m, warnPercent: 50, hardPercent: 150).State.ShouldBe(BudgetState.Warn);
        Evaluate("daily", 10m, limit: 10m, warnPercent: 50, hardPercent: 150).State.ShouldBe(BudgetState.Soft);
        Evaluate("daily", 14.99m, limit: 10m, warnPercent: 50, hardPercent: 150).State.ShouldBe(BudgetState.Soft);
        Evaluate("daily", 15m, limit: 10m, warnPercent: 50, hardPercent: 150).State.ShouldBe(BudgetState.Hard);
    }

    [Fact]
    public void Hard_percent_of_exactly_100_makes_100_percent_hard()
    {
        Evaluate("daily", 100m, hardPercent: 100).State.ShouldBe(BudgetState.Hard);
    }

    [Fact]
    public void Period_reports_hard_cap_and_rounded_percent()
    {
        var status = Evaluate("daily", 33.335m, limit: 50m, hardPercent: 120);

        status.HardCap.ShouldBe(60m);
        status.Percent.ShouldBe(67);
    }

    [Fact]
    public void Overall_state_is_the_more_severe_of_the_two_periods()
    {
        new BudgetStatus(Evaluate("daily", 120m), Evaluate("monthly", 10m)).Overall.ShouldBe(BudgetState.Hard);
        new BudgetStatus(Evaluate("daily", 10m), Evaluate("monthly", 100m)).Overall.ShouldBe(BudgetState.Soft);
        new BudgetStatus(Evaluate("daily", 85m), Evaluate("monthly", 10m)).Overall.ShouldBe(BudgetState.Warn);
        new BudgetStatus(Evaluate("daily", 10m), Evaluate("monthly", 10m)).Overall.ShouldBe(BudgetState.Normal);
    }

    [Fact]
    public void Binding_period_is_monthly_when_it_is_at_least_as_severe_otherwise_daily()
    {
        new BudgetStatus(Evaluate("daily", 120m), Evaluate("monthly", 100m)).Binding.Kind.ShouldBe("daily");
        new BudgetStatus(Evaluate("daily", 120m), Evaluate("monthly", 120m)).Binding.Kind.ShouldBe("monthly");
        new BudgetStatus(Evaluate("daily", 10m), Evaluate("monthly", 130m)).Binding.Kind.ShouldBe("monthly");
    }
}
