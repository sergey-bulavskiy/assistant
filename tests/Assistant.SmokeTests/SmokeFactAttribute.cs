namespace Assistant.SmokeTests;

/// <summary>
/// A fact that is skipped unless SMOKE=1, so a plain <c>dotnet test</c> never starts containers or
/// logs in to Telegram.
/// </summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SMOKE") != "1")
        {
            Skip = "Smoke test is opt-in: set SMOKE=1 (see tests/Assistant.SmokeTests/README.md).";
        }
    }
}
