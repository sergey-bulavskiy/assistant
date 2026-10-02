namespace Assistant.Application.Health;

/// <summary>The profile's IANA time zone: "today" for the stage week, and /settz validation.</summary>
public static class ProfileTimeZone
{
    /// <summary>Today's date in the zone; an id the runtime does not know falls back to UTC.</summary>
    public static DateOnly LocalToday(DateTimeOffset utcNow, string timeZoneId)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }

    /// <summary>Accepts "Area/City" ids and "UTC" that the runtime knows; returns the trimmed text.</summary>
    public static bool TryNormalize(string input, out string timeZoneId)
    {
        timeZoneId = string.Empty;
        var candidate = input.Trim();
        if (candidate != "UTC" && !candidate.Contains('/'))
        {
            return false;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out _))
        {
            return false;
        }

        timeZoneId = candidate;
        return true;
    }
}
