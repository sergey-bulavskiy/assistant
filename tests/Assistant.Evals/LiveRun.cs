using Assistant.Application.Health;

namespace Assistant.Evals;

/// <summary>The outcome of one live run: one result per case, the printed lines, and whether the
/// quality bar was met.</summary>
public sealed record LiveReport(IReadOnlyList<CaseResult> Results, IReadOnlyList<string> Lines)
{
    public int Passed => Results.Count(r => r.Passed);

    public int FailedCritical => Results.Count(r => !r.Passed && r.Critical);

    /// <summary>Bar for using a model as the fast extraction model: at least 90% of the cases pass and
    /// no critical case fails.</summary>
    public bool MeetsBar => Passed * 10 >= Results.Count * 9 && FailedCritical == 0;
}

/// <summary>Sends every case to a model (a delegate, so tests can pass a fake) and checks each answer.
/// With record, the answers of the passing cases become their recorded answers and every case file is
/// rewritten; failed cases keep their old recorded answer.</summary>
public static class LiveRun
{
    /// <summary>(system prompt, message text, cancellation) → the model's answer text.</summary>
    public delegate Task<string> Complete(string systemPrompt, string text, CancellationToken cancellationToken);

    public static async Task<LiveReport> RunAsync(
        IReadOnlyList<EvalCase> cases,
        string instructions,
        Complete complete,
        bool record,
        Action<string> write,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var results = new List<CaseResult>();
        foreach (var evalCase in cases)
        {
            // The message has just arrived: "now" for the prompt is its send time, as in production.
            var sentAt = evalCase.SentAtUtc;
            var prompt = ExtractionPrompt.BuildSystemPrompt(instructions, sentAt, sentAt, evalCase.TimeZone);
            CaseResult result;
            try
            {
                var answer = await complete(prompt, evalCase.Text, cancellationToken);
                result = ExtractionCheck.Run(evalCase, answer);
                if (record && result.Passed)
                {
                    evalCase.SetRecordedOutput(answer);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A cancelled run stops: the filter lets that exception through. Type only: an exception message may carry a provider's text.
                result = new CaseResult(evalCase.Id, evalCase.Critical, new[] { $"model call failed: {ex.GetType().Name}" });
            }

            results.Add(result);
            var line = result.Passed
                ? $"PASS {result.CaseId}"
                : $"FAIL {result.CaseId}{(result.Critical ? " (critical)" : string.Empty)}: {string.Join("; ", result.Problems)}";
            lines.Add(line);
            write(line);
        }

        var report = new LiveReport(results, lines);
        var totals = $"Passed {report.Passed} of {results.Count}; failed {results.Count - report.Passed}, of them critical {report.FailedCritical}.";
        lines.Add(totals);
        write(totals);

        if (record)
        {
            foreach (var file in cases.GroupBy(c => c.SourceFile ?? throw new InvalidOperationException($"Case '{c.Id}' has no source file to record into.")))
            {
                CaseFiles.Save(file.Key, file);
            }
        }

        return report;
    }
}
