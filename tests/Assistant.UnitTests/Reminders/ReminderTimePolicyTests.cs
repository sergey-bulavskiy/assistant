using Assistant.Application.Reminders;

namespace Assistant.UnitTests.Reminders;

public sealed class ReminderTimePolicyTests
{
    [Theory]
    [InlineData("2026-01-02T11:29:59Z", 870, 180, "2026-01-02T11:30:00Z")]
    [InlineData("2026-01-02T11:30:00Z", 870, 180, "2026-01-03T11:30:00Z")]
    [InlineData("2026-01-02T11:30:01Z", 870, 180, "2026-01-03T11:30:00Z")]
    [InlineData("2026-01-02T23:59:59Z", 0, 0, "2026-01-03T00:00:00Z")]
    [InlineData("2026-01-02T09:59:59Z", 0, 840, "2026-01-02T10:00:00Z")]
    [InlineData("2026-01-02T11:59:59Z", 0, -720, "2026-01-02T12:00:00Z")]
    [InlineData("2026-12-31T23:30:00Z", 0, 60, "2027-01-01T23:00:00Z")]
    [InlineData("2028-02-28T23:59:59Z", 0, 0, "2028-02-29T00:00:00Z")]
    public void Daily_due_is_literal_next_future_utc_instant(string now, int minute, int offset, string expected)
    {
        var due = ReminderTimePolicy.NextDaily(DateTimeOffset.Parse(now), minute, offset);
        due.ShouldBe(DateTimeOffset.Parse(expected));
        due.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1440, 0)]
    [InlineData(0, -721)]
    [InlineData(0, 841)]
    public void Daily_policy_rejects_outside_time_and_offset_bounds(int minute, int offset) =>
        Should.Throw<ArgumentOutOfRangeException>(() => ReminderTimePolicy.NextDaily(
            DateTimeOffset.Parse("2026-01-02T12:00:00Z"), minute, offset));

    [Theory]
    [InlineData("2026-01-02T21:59:59Z", 0, 1320, 480, false)]
    [InlineData("2026-01-02T22:00:00Z", 0, 1320, 480, true)]
    [InlineData("2026-01-03T07:59:59Z", 0, 1320, 480, true)]
    [InlineData("2026-01-03T08:00:00Z", 0, 1320, 480, false)]
    [InlineData("2026-01-02T08:59:59Z", 0, 540, 1020, false)]
    [InlineData("2026-01-02T09:00:00Z", 0, 540, 1020, true)]
    [InlineData("2026-01-02T16:59:59Z", 0, 540, 1020, true)]
    [InlineData("2026-01-02T17:00:00Z", 0, 540, 1020, false)]
    [InlineData("2026-01-02T19:00:00Z", 180, 1320, 480, true)]
    [InlineData("2026-01-03T05:00:00Z", 180, 1320, 480, false)]
    public void Quiet_hours_use_current_offset_start_inclusive_end_exclusive(
        string now, int offset, int start, int end, bool expected) =>
        ReminderTimePolicy.IsQuiet(DateTimeOffset.Parse(now), new(offset, start, end)).ShouldBe(expected);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Stale_boundary_is_inclusive_at_twenty_four_hours(int seconds, bool expected)
    {
        var due = DateTimeOffset.Parse("2026-01-02T12:00:00Z");
        ReminderTimePolicy.IsStale(due, due.AddHours(24).AddSeconds(seconds)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Draft_expiry_boundary_is_inclusive_at_twenty_four_hours(int seconds, bool expected)
    {
        var created = DateTimeOffset.Parse("2026-01-02T12:00:00Z");
        ReminderTimePolicy.IsDraftExpired(created, created.AddHours(24).AddSeconds(seconds)).ShouldBe(expected);
    }
}
