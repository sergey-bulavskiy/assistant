using Assistant.Infrastructure.Roles;
using Xunit.Abstractions;

namespace Assistant.Evals;

/// <summary>Opt-in (EVALS_LIVE=1), local only, never in CI: every case (public file plus
/// EVALS_CASES_FILE) goes to the model of EVALS_LLM_MODELS with the real extract.md prompt. Prints
/// PASS/FAIL per case and the totals; fails unless at least 90% of the cases and every critical case
/// pass. EVALS_RECORD=1 stores the answers of the passing cases as their recorded answers.</summary>
public sealed class ExtractionLiveTests
{
    public const string RecordVariable = "EVALS_RECORD";

    private readonly ITestOutputHelper _output;

    public ExtractionLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [EvalsLiveFact]
    public async Task Live_model_meets_the_quality_bar()
    {
        var cases = CaseFiles.LoadAll();
        var record = Environment.GetEnvironmentVariable(RecordVariable) == "1";
        var instructions = new RolePrompts(typeof(RolePrompts).Assembly).Find("health", "extract.md")
            ?? throw new InvalidOperationException("roles/health/extract.md is not embedded.");
        using var model = LiveModel.FromEnvironment();

        var header = $"Model: {model.Name}";
        _output.WriteLine(header);
        var report = await LiveRun.RunAsync(cases, instructions, model.CompleteAsync, record, _output.WriteLine, CancellationToken.None);

        report.MeetsBar.ShouldBeTrue(string.Join("\n", report.Lines.Prepend(header)));
    }
}
