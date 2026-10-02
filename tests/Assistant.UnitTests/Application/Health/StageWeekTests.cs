using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class StageWeekTests
{
    private static readonly DateOnly Today = new(2030, 2, 7);

    [Fact]
    public void Not_set_without_a_start_date()
    {
        var result = StageWeek.Compute(Today, null);

        result.Status.ShouldBe(StageWeekStatus.NotSet);
        result.Describe().ShouldBe("не задана (/setstart)");
    }

    [Theory]
    [InlineData("2030-02-07", "0 нед. 0 дн.")]
    [InlineData("2030-01-15", "3 нед. 2 дн.")]
    [InlineData("2029-04-13", "42 нед. 6 дн.")]
    public void Counts_whole_weeks_and_days(string start, string expected)
    {
        var result = StageWeek.Compute(Today, DateOnly.Parse(start));

        result.Status.ShouldBe(StageWeekStatus.Valid);
        result.Describe().ShouldBe(expected);
    }

    [Theory]
    [InlineData("2030-02-08")]
    [InlineData("2029-04-12")]
    public void Outside_0_to_300_days_is_out_of_range(string start)
    {
        var result = StageWeek.Compute(Today, DateOnly.Parse(start));

        result.Status.ShouldBe(StageWeekStatus.OutOfRange);
        result.Describe().ShouldBe("не определена — проверьте дату (/setstart)");
    }
}
