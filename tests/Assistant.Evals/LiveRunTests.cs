using System.Reflection;
using System.Text.Json.Nodes;

namespace Assistant.Evals;

/// <summary>The live loop with a fake model (a delegate returning canned answers): no provider, no
/// network, no key.</summary>
public sealed class LiveRunTests : IDisposable
{
    private const string Instructions = "test instructions 4711";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LiveRunTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Line(string id, string text, decimal kg, string? recorded = null) =>
        $$$"""{"id":"{{{id}}}","now":"2030-02-07T09:00","time_zone":"UTC","text":"{{{text}}}","critical":false,"expected":{"events":[{"type":"weight","kg":{{{kg}}}}],"unclear":[]}{{{(recorded is null ? "" : $",\"recorded_output\":\"{recorded}\"")}}}}""";

    private static string WeightAnswer(decimal kg) =>
        $$$"""{"events":[{"type":"weight","day":0,"time":null,"kg":{{{kg}}}}],"unclear":[],"is_question":false}""";

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    // Right for "вес 70" and "вес 72", wrong for "вес 71", throws for "вес 73".
    private static Task<string> FakeModel(string systemPrompt, string text, CancellationToken cancellationToken)
    {
        systemPrompt.ShouldContain(Instructions);
        return text switch
        {
            "вес 70" => Task.FromResult(WeightAnswer(70)),
            "вес 71" => Task.FromResult(WeightAnswer(70)),
            "вес 72" => Task.FromResult(WeightAnswer(72)),
            _ => throw new HttpRequestException("canned failure")
        };
    }

    private (string PublicFile, string PrivateFile) WriteFiles() =>
        (Write("public.jsonl",
                Line("w-pass", "вес 70", 70, "old-a"),
                Line("w-wrong", "вес 71", 71, "old-b"),
                Line("w-error", "вес 73", 73, "old-c")),
            Write("private.jsonl", Line("p-pass", "вес 72", 72)));

    [Fact]
    public async Task Record_rewrites_only_the_passing_cases_in_each_file()
    {
        var (publicFile, privateFile) = WriteFiles();
        var cases = CaseFiles.LoadAll(publicFile, privateFile);

        var report = await LiveRun.RunAsync(cases, Instructions, FakeModel, record: true, _ => { }, CancellationToken.None);

        report.Results.Where(r => r.Passed).Select(r => r.CaseId).ShouldBe(["w-pass", "p-pass"]);
        var publicCases = CaseFiles.Load(publicFile);
        publicCases.Select(c => c.Id).ShouldBe(["w-pass", "w-wrong", "w-error"]);
        publicCases[0].Json["recorded_output"].ShouldBeOfType<JsonObject>();
        ExtractionCheck.Run(publicCases[0], publicCases[0].RecordedOutput).Passed.ShouldBeTrue();
        publicCases[1].RecordedOutput.ShouldBe("old-b");
        publicCases[2].RecordedOutput.ShouldBe("old-c");
        var privateCase = CaseFiles.Load(privateFile).ShouldHaveSingleItem();
        privateCase.Json["recorded_output"]!["events"]![0]!["kg"]!.GetValue<decimal>().ShouldBe(72m);
    }

    [Fact]
    public async Task Without_record_no_file_changes()
    {
        var (publicFile, privateFile) = WriteFiles();
        var publicBefore = File.ReadAllText(publicFile);
        var privateBefore = File.ReadAllText(privateFile);

        await LiveRun.RunAsync(CaseFiles.LoadAll(publicFile, privateFile), Instructions, FakeModel, record: false, _ => { }, CancellationToken.None);

        File.ReadAllText(publicFile).ShouldBe(publicBefore);
        File.ReadAllText(privateFile).ShouldBe(privateBefore);
    }

    [Fact]
    public async Task Report_prints_each_case_and_names_a_failed_call_by_type_only()
    {
        var (publicFile, privateFile) = WriteFiles();
        var written = new List<string>();

        var report = await LiveRun.RunAsync(CaseFiles.LoadAll(publicFile, privateFile), Instructions, FakeModel, record: false, written.Add, CancellationToken.None);

        written.ShouldBe(report.Lines);
        written[0].ShouldBe("PASS w-pass");
        written[1].ShouldStartWith("FAIL w-wrong: ");
        written[2].ShouldBe("FAIL w-error: model call failed: HttpRequestException");
        written[3].ShouldBe("PASS p-pass");
        written[4].ShouldBe("Passed 2 of 4; failed 2, of them critical 0.");
        string.Join("\n", written).ShouldNotContain("canned failure");
        report.MeetsBar.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_run_stops_instead_of_reporting_failed_cases()
    {
        var (publicFile, privateFile) = WriteFiles();
        var calls = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => LiveRun.RunAsync(
            CaseFiles.LoadAll(publicFile, privateFile),
            Instructions,
            (_, _, token) =>
            {
                calls++;
                token.ThrowIfCancellationRequested();
                return Task.FromResult(WeightAnswer(70));
            },
            record: false,
            _ => { },
            cts.Token));

        calls.ShouldBe(1);
    }

    private static CaseResult Ok(string id, bool critical = false) => new(id, critical, []);

    private static CaseResult Bad(string id, bool critical = false) => new(id, critical, ["wrong"]);

    [Fact]
    public void Bar_needs_ninety_percent_and_every_critical_case()
    {
        var nineOfTen = Enumerable.Range(1, 9).Select(i => Ok($"c{i}")).Append(Bad("c10")).ToList();
        var eightOfTen = Enumerable.Range(1, 8).Select(i => Ok($"c{i}")).Append(Bad("c9")).Append(Bad("c10")).ToList();
        var criticalFailed = Enumerable.Range(1, 19).Select(i => Ok($"c{i}")).Append(Bad("c20", critical: true)).ToList();

        new LiveReport(nineOfTen, []).MeetsBar.ShouldBeTrue();
        new LiveReport(eightOfTen, []).MeetsBar.ShouldBeFalse();
        new LiveReport(criticalFailed, []).MeetsBar.ShouldBeFalse();
    }

    [Fact]
    public void Live_test_is_skipped_unless_evals_live_is_exactly_one()
    {
        EvalsLiveFactAttribute.SkipReason(null).ShouldNotBeNull();
        EvalsLiveFactAttribute.SkipReason("").ShouldNotBeNull();
        EvalsLiveFactAttribute.SkipReason("0").ShouldNotBeNull();
        EvalsLiveFactAttribute.SkipReason("true").ShouldNotBeNull();
        EvalsLiveFactAttribute.SkipReason("1").ShouldBeNull();
    }

    [Fact]
    public void Every_test_that_can_reach_a_model_is_gated()
    {
        var tests = typeof(ExtractionLiveTests).GetMethods()
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
            .ToList();

        tests.ShouldNotBeEmpty();
        tests.ShouldAllBe(m => m.GetCustomAttribute<EvalsLiveFactAttribute>() != null);
    }
}
