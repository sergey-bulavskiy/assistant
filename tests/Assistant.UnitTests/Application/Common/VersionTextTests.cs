using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class VersionTextTests
{
    [Fact]
    public void Formats_sha_build_time_and_uptime()
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var buildTime = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);
        var buildInfo = new BuildInfo("abcdef1234567", buildTime, started);
        var now = started.AddDays(1).AddHours(2).AddMinutes(3);

        VersionText.Format(buildInfo, now).ShouldBe("abcdef1 · built 2026-01-01 12:30 UTC · up 1d 2h 3m");
    }

    [Fact]
    public void Shows_unknown_when_build_time_is_missing()
    {
        var started = DateTimeOffset.UtcNow;
        var buildInfo = new BuildInfo("dev", null, started);

        VersionText.Format(buildInfo, started).ShouldBe("dev · built unknown · up 0d 0h 0m");
    }

    [Fact]
    public void Short_sha_is_at_most_seven_characters()
    {
        new BuildInfo("abcdef1234567", null, DateTimeOffset.UtcNow).ShortSha.ShouldBe("abcdef1");
    }
}
