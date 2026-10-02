namespace Assistant.Application.Health;

public enum StageWeekStatus
{
    NotSet,
    OutOfRange,
    Valid
}

/// <summary>Days since the stage start date; shown as "N нед. M дн.".</summary>
public sealed record StageWeekResult(StageWeekStatus Status, int TotalDays)
{
    public int Weeks => TotalDays / 7;

    public int Days => TotalDays % 7;

    public string Describe() => Status switch
    {
        StageWeekStatus.Valid => $"{Weeks} нед. {Days} дн.",
        StageWeekStatus.NotSet => "не задана (/setstart)",
        _ => "не определена — проверьте дату (/setstart)"
    };
}

public static class StageWeek
{
    /// <summary>The last valid day (inclusive). Anything outside 0..MaxDays is out of range.</summary>
    public const int MaxDays = 300;

    /// <summary>Pure. <paramref name="today"/> is a date in the profile's time zone (a later rebuild
    /// passes each message's own local date).</summary>
    public static StageWeekResult Compute(DateOnly today, DateOnly? start)
    {
        if (start is not { } startDate)
        {
            return new StageWeekResult(StageWeekStatus.NotSet, 0);
        }

        var days = today.DayNumber - startDate.DayNumber;
        return days is < 0 or > MaxDays
            ? new StageWeekResult(StageWeekStatus.OutOfRange, days)
            : new StageWeekResult(StageWeekStatus.Valid, days);
    }
}
