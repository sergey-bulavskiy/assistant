namespace Assistant.Application.Reminders;

public static class ReminderTimePolicy
{
    public static DateTimeOffset NextDaily(DateTimeOffset now, int minute, int offsetMinutes)
    {
        if (minute is < 0 or >= 1440 || offsetMinutes is < -720 or > 840)
            throw new ArgumentOutOfRangeException(nameof(minute));
        var local = now.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        var date = local.Date.AddMinutes(minute);
        var next = new DateTimeOffset(date, local.Offset).ToUniversalTime();
        return next <= now ? next.AddDays(1) : next;
    }
    public static bool IsQuiet(DateTimeOffset now, ReminderPreferences preferences)
    {
        var local = now.ToOffset(TimeSpan.FromMinutes(preferences.OffsetMinutes));
        var minute = local.Hour * 60 + local.Minute;
        var start = preferences.QuietStartMinute;
        var end = preferences.QuietEndMinute;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }
    public static bool IsStale(DateTimeOffset due, DateTimeOffset now) => due <= now.AddHours(-24);
    public static bool IsDraftExpired(DateTimeOffset created, DateTimeOffset now) => created <= now.AddHours(-24);
}
