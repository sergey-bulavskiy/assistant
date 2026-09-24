using System.Globalization;

namespace Assistant.Application.Common;

public static class VersionText
{
    public static string Format(BuildInfo buildInfo, DateTimeOffset now)
    {
        var built = buildInfo.BuildTime.HasValue
            ? buildInfo.BuildTime.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : "unknown";

        var uptime = now - buildInfo.StartedAt;
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        var uptimeText = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        return $"{buildInfo.ShortSha} · built {built} · up {uptimeText}";
    }
}
