using System.Globalization;

namespace Assistant.Application.Common;

public record BuildInfo(string Sha, DateTimeOffset? BuildTime, DateTimeOffset StartedAt)
{
    public string ShortSha => Sha.Length <= 7 ? Sha : Sha[..7];

    public static BuildInfo FromEnvironment(IClock clock)
    {
        var sha = Environment.GetEnvironmentVariable("GIT_SHA");
        if (string.IsNullOrWhiteSpace(sha))
        {
            sha = "dev";
        }

        DateTimeOffset? buildTime = null;
        var buildTimeRaw = Environment.GetEnvironmentVariable("BUILD_TIME");
        if (!string.IsNullOrWhiteSpace(buildTimeRaw) &&
            DateTimeOffset.TryParse(buildTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            buildTime = parsed.ToUniversalTime();
        }

        return new BuildInfo(sha, buildTime, clock.UtcNow);
    }
}
