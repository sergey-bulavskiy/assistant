namespace Assistant.Evals;

/// <summary>A fact that is skipped unless EVALS_LIVE=1, so a plain dotnet test (CI included) never
/// calls a model.</summary>
public sealed class EvalsLiveFactAttribute : FactAttribute
{
    public const string Variable = "EVALS_LIVE";

    public EvalsLiveFactAttribute()
    {
        Skip = SkipReason(Environment.GetEnvironmentVariable(Variable));
    }

    /// <summary>The skip reason for this value of EVALS_LIVE; null (run) only for exactly "1".</summary>
    public static string? SkipReason(string? value) =>
        value == "1"
            ? null
            : "Live extraction evals are opt-in: set EVALS_LIVE=1 (see tests/Assistant.Evals/README.md).";
}
