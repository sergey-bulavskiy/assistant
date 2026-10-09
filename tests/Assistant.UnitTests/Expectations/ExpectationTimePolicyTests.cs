using Assistant.Application.Expectations;

namespace Assistant.UnitTests.Expectations;

public sealed class ExpectationTimePolicyTests
{
    [Fact]
    public void Tomorrow_only_window_uses_frozen_offset_and_grace()
    {
        var tomorrow = ExpectationTimePolicy.Tomorrow(DateTimeOffset.Parse("2032-02-09T12:00:00Z"), 180);
        tomorrow.ShouldBe(new DateOnly(2032, 2, 10));
        var window = ExpectationTimePolicy.Window(tomorrow, 540, 30, 180);
        window.ShouldBe(new ExpectationWindow(new(2032, 2, 10),
            DateTimeOffset.Parse("2032-02-09T21:00:00Z"),
            DateTimeOffset.Parse("2032-02-10T06:30:00Z"),
            DateTimeOffset.Parse("2032-02-10T21:00:00Z")));
    }

    [Fact]
    public void Zero_grace_is_due_at_deadline()
    {
        ExpectationTimePolicy.Window(new(2032, 2, 10), 540, 0, 180).ShouldBe(new ExpectationWindow(
            new(2032, 2, 10), DateTimeOffset.Parse("2032-02-09T21:00:00Z"),
            DateTimeOffset.Parse("2032-02-10T06:00:00Z"), DateTimeOffset.Parse("2032-02-10T21:00:00Z")));
    }

    [Theory]
    [InlineData("2032-02-10T06:29:59.999Z", false)]
    [InlineData("2032-02-10T06:30:00Z", true)]
    [InlineData("2032-02-10T20:59:59.999Z", true)]
    [InlineData("2032-02-10T21:00:00Z", false)]
    [InlineData("2032-02-10T21:00:00.001Z", false)]
    public void Eligibility_includes_due_and_excludes_next_local_midnight(string now, bool expected)
    {
        var window = new ExpectationWindow(new(2032, 2, 10), DateTimeOffset.Parse("2032-02-09T21:00:00Z"),
            DateTimeOffset.Parse("2032-02-10T06:30:00Z"), DateTimeOffset.Parse("2032-02-10T21:00:00Z"));
        ExpectationTimePolicy.Eligible(window, DateTimeOffset.Parse(now)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2031-12-31T23:30:00Z", 840, 2032, 1, 1, 2032, 1, 2)]
    [InlineData("2032-01-01T00:30:00Z", -720, 2031, 12, 31, 2032, 1, 1)]
    [InlineData("2032-02-29T23:30:00Z", 180, 2032, 3, 1, 2032, 3, 2)]
    public void Extreme_offsets_and_rollover_use_local_dates(string now, int offset,
        int year, int month, int day, int tomorrowYear, int tomorrowMonth, int tomorrowDay)
    {
        var instant = DateTimeOffset.Parse(now);
        ExpectationTimePolicy.LocalDate(instant, offset).ShouldBe(new DateOnly(year, month, day));
        ExpectationTimePolicy.Tomorrow(instant, offset).ShouldBe(new DateOnly(tomorrowYear, tomorrowMonth, tomorrowDay));
    }

    [Theory]
    [InlineData(-720, "2032-02-10T12:00:00Z", "2032-02-10T21:30:00Z", "2032-02-11T12:00:00Z")]
    [InlineData(840, "2032-02-09T10:00:00Z", "2032-02-09T19:30:00Z", "2032-02-10T10:00:00Z")]
    public void Extreme_offset_windows_have_literal_utc_bounds(int offset, string start, string due, string expires)
    {
        ExpectationTimePolicy.Window(new(2032, 2, 10), 540, 30, offset).ShouldBe(new ExpectationWindow(
            new(2032, 2, 10), DateTimeOffset.Parse(start), DateTimeOffset.Parse(due), DateTimeOffset.Parse(expires)));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1440, 0, 0)]
    [InlineData(540, -1, 0)]
    [InlineData(540, 181, 0)]
    [InlineData(1410, 30, 0)]
    [InlineData(1439, 1, 0)]
    [InlineData(540, 0, -721)]
    [InlineData(540, 0, 841)]
    public void Invalid_window_parameters_fail_before_building_a_window(int minute, int grace, int offset)
    {
        var ex = Should.Throw<ArgumentException>(() => ExpectationTimePolicy.Window(new(2032, 2, 10), minute, grace, offset));
        ex.Message.ShouldBe("Invalid expectation schedule.");
    }

    [Fact]
    public void Last_local_minute_is_valid_without_grace()
    {
        ExpectationTimePolicy.Window(new(2032, 2, 10), 1439, 0, 0).Due
            .ShouldBe(DateTimeOffset.Parse("2032-02-10T23:59:00Z"));
    }

    [Theory]
    [InlineData("2032-02-10T11:59:59.999Z", false)]
    [InlineData("2032-02-10T12:00:00Z", true)]
    [InlineData("2032-02-10T12:00:00.001Z", true)]
    public void Preview_age_expires_inclusively_at_twenty_four_hours(string now, bool expected)
    {
        // Match tomorrow at the evaluated instant to isolate age from the separate midnight rule.
        ExpectationTimePolicy.PreviewExpired(DateTimeOffset.Parse("2032-02-09T12:00:00Z"),
            new(2032, 2, 11), 180, DateTimeOffset.Parse(now)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2032-02-09T20:59:59.999Z", false)]
    [InlineData("2032-02-09T21:00:00Z", true)]
    public void Preview_cannot_save_an_effective_date_that_is_no_longer_tomorrow(string now, bool expected)
    {
        ExpectationTimePolicy.PreviewExpired(DateTimeOffset.Parse("2032-02-09T12:00:00Z"),
            new(2032, 2, 10), 180, DateTimeOffset.Parse(now)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(540, "09:00")]
    [InlineData(1439, "23:59")]
    public void Preview_time_format_is_fixed_width(int minute, string text)
    {
        ExpectationTimePolicy.Minute(minute).ShouldBe(text);
    }

    [Theory]
    [InlineData(-720, "-12:00")]
    [InlineData(-30, "-00:30")]
    [InlineData(0, "+00:00")]
    [InlineData(180, "+03:00")]
    [InlineData(840, "+14:00")]
    public void Preview_offset_format_includes_sign_and_minutes(int offset, string text)
    {
        ExpectationTimePolicy.Offset(offset).ShouldBe(text);
    }
}
