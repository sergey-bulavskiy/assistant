namespace Assistant.Application.Health;

/// <summary>The profile's IANA time zone: "today", local times of events, and /settz validation.</summary>
public static class ProfileTimeZone
{
    /// <summary>The zone for an id; an id the runtime does not know falls back to UTC.</summary>
    public static TimeZoneInfo Find(string timeZoneId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) ? found : TimeZoneInfo.Utc;

    /// <summary>The id as shown to the model: the stored id, or "UTC" when the runtime does not know it.</summary>
    public static string DisplayId(string timeZoneId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _) ? timeZoneId : "UTC";

    /// <summary>Today's date in the zone; an id the runtime does not know falls back to UTC.</summary>
    public static DateOnly LocalToday(DateTimeOffset utcNow, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Find(timeZoneId)).DateTime);

    /// <summary>UTC instant of local midnight of <paramref name="date"/> (the first valid local time
    /// if midnight is skipped by a clock change).</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date, string timeZoneId)
    {
        var zone = Find(timeZoneId);
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
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
