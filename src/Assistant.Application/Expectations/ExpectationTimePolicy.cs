using System.Globalization;

namespace Assistant.Application.Expectations;

public sealed record ExpectationWindow(DateOnly Date, DateTimeOffset Start,
    DateTimeOffset Due, DateTimeOffset Expires);

public static class ExpectationTimePolicy
{
    public static void Validate(int deadlineMinute, int graceMinutes, int offsetMinutes)
    {
        if (deadlineMinute is < 0 or >= 1440 || graceMinutes is < 0 or > 180
            || deadlineMinute + graceMinutes >= 1440 || offsetMinutes is < -720 or > 840)
            throw new ArgumentException("Invalid expectation schedule.");
    }

    public static DateOnly LocalDate(DateTimeOffset now, int offsetMinutes) =>
        DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).DateTime);

    public static DateOnly Tomorrow(DateTimeOffset now, int offsetMinutes) =>
        LocalDate(now, offsetMinutes).AddDays(1);

    public static ExpectationWindow Window(DateOnly date, int deadlineMinute,
        int graceMinutes, int offsetMinutes)
    {
        Validate(deadlineMinute, graceMinutes, offsetMinutes);
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
        return new(date, start, start.AddMinutes(deadlineMinute + graceMinutes), start.AddDays(1));
    }

    public static bool Eligible(ExpectationWindow window, DateTimeOffset now) =>
        now >= window.Due && now < window.Expires;

    public static bool PreviewExpired(DateTimeOffset createdAt, DateOnly effectiveDate,
        int offsetMinutes, DateTimeOffset now) =>
        now >= createdAt.AddHours(24) || effectiveDate != Tomorrow(now, offsetMinutes);

    public static string Minute(int value) => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(value))
        .ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Offset(int value) => (value < 0 ? "-" : "+")
        + TimeSpan.FromMinutes(Math.Abs(value)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
