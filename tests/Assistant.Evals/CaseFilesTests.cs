using System.Text.Json.Nodes;
using Assistant.Domain.Health;

namespace Assistant.Evals;

public sealed class CaseFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public CaseFilesTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Line(string id, string text = "test") =>
        $$$"""{"id":"{{{id}}}","now":"2030-02-07T09:00","time_zone":"UTC","text":"{{{text}}}","critical":false,"expected":{"events":[],"unclear":[]}}""";

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join('\n', lines));
        return path;
    }

    [Fact]
    public void Load_skips_blank_lines_and_names_the_bad_line()
    {
        var bad = Write("bad.jsonl", Line("t1"), "", """{"id":"t2"}""");
        var good = Write("good.jsonl", Line("t1"), "");

        Should.Throw<FormatException>(() => CaseFiles.Load(bad)).Message.ShouldContain("line 3");
        CaseFiles.Load(good).Count.ShouldBe(1);
    }

    [Fact]
    public void Save_then_load_keeps_the_cases()
    {
        var source = Write("in.jsonl", Line("a", "вес 70"), Line("b"));
        var path = Path.Combine(_dir, "out.jsonl");

        CaseFiles.Save(path, CaseFiles.Load(source));

        var loaded = CaseFiles.Load(path);
        loaded.Select(c => c.Id).ShouldBe(["a", "b"]);
        loaded[0].Text.ShouldBe("вес 70");
        var saved = File.ReadAllText(path);
        saved.ShouldContain("вес 70");
        saved.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
    }

    [Fact]
    public void A_private_file_inside_the_repository_is_refused()
    {
        var publicFile = Write("public.jsonl", Line("a"));

        Should.Throw<InvalidOperationException>(
                () => CaseFiles.LoadAll(publicFile, Path.Combine(CaseFiles.RepoRoot(), "private.jsonl")))
            .Message.ShouldContain("outside the repository");
    }

    [Fact]
    public void A_private_file_inside_any_checkout_is_refused_and_a_sibling_folder_is_accepted()
    {
        var otherCheckout = Path.Combine(_dir, "other-checkout");
        Directory.CreateDirectory(Path.Combine(otherCheckout, "nested"));
        File.WriteAllText(Path.Combine(otherCheckout, "Assistant.slnx"), "");
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);
        var publicFile = Write("public.jsonl", Line("a"));
        var inside = Path.Combine(otherCheckout, "nested", "private.jsonl");
        var outside = Path.Combine(plain, "private.jsonl");
        File.WriteAllText(inside, Line("b"));
        File.WriteAllText(outside, Line("c"));

        Should.Throw<InvalidOperationException>(() => CaseFiles.LoadAll(publicFile, inside))
            .Message.ShouldContain("outside the repository");
        CaseFiles.LoadAll(publicFile, outside).Select(c => c.Id).ShouldBe(["a", "c"]);
    }

    [Fact]
    public void A_private_file_reached_through_a_folder_link_into_a_checkout_is_refused()
    {
        var otherCheckout = Path.Combine(_dir, "other-checkout");
        var nested = Path.Combine(otherCheckout, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(otherCheckout, "Assistant.slnx"), "");
        File.WriteAllText(Path.Combine(nested, "private.jsonl"), Line("b"));
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);
        var dirLink = Path.Combine(plain, "dir-link");
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no privilege, unlike a symbolic link.
            using var mklink = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dirLink}\" \"{nested}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(dirLink, nested);
        }

        var publicFile = Write("public.jsonl", Line("a"));

        try
        {
            Directory.Exists(dirLink).ShouldBeTrue();
            Should.Throw<InvalidOperationException>(() => CaseFiles.LoadAll(publicFile, Path.Combine(dirLink, "private.jsonl")))
                .Message.ShouldContain("outside the repository");
        }
        finally
        {
            Directory.Delete(dirLink); // Removes the link only, so the cleanup in Dispose stays safe.
        }
    }

    [Fact]
    public void A_failed_save_leaves_the_file_intact_and_no_temp_file()
    {
        var path = Write("cases.jsonl", Line("a"));
        var before = File.ReadAllText(path);
        var cases = CaseFiles.Load(path);
        // A folder where the temp file should go makes writing the temp file fail.
        Directory.CreateDirectory(path + ".tmp");

        Should.Throw<Exception>(() => CaseFiles.Save(path, cases));

        File.ReadAllText(path).ShouldBe(before);
        Directory.Exists(path + ".tmp").ShouldBeTrue();
    }

    [Fact]
    public void Save_writes_utf8_without_bom_and_lf_endings_and_leaves_no_temp_file()
    {
        var path = Write("cases.jsonl", Line("a", "вес 70"), Line("b"));

        CaseFiles.Save(path, CaseFiles.Load(path));

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).ToArray().ShouldNotBe([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
        bytes.ShouldNotContain((byte)'\r');
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Public_file_has_at_least_forty_cases_each_with_a_recorded_answer()
    {
        var cases = CaseFiles.Load(CaseFiles.PublicFile());

        cases.Count.ShouldBeGreaterThanOrEqualTo(40);
        cases.Where(c => c.RecordedOutput is null).Select(c => c.Id).ShouldBeEmpty();
        cases.Select(c => c.Id).Distinct().Count().ShouldBe(cases.Count);
    }

    [Fact]
    public void Public_file_covers_every_metric_and_outcome()
    {
        var cases = CaseFiles.Load(CaseFiles.PublicFile());

        var types = cases.SelectMany(c => c.Expected["events"]!.AsArray())
            .Select(e => e!["type"]!.GetValue<string>())
            .ToHashSet();
        types.ShouldBe(HealthEventTypes.All, ignoreOrder: true);

        var reasons = cases.SelectMany(c => c.Expected["unclear"]!.AsArray()).Select(r => r!.GetValue<string>()).ToHashSet();
        reasons.ShouldContain("unit");
        reasons.ShouldContain("value");
        reasons.ShouldContain("time");
        reasons.ShouldContain("type");

        cases.Any(c => c.Expected["is_question"] is { } q && q.GetValue<bool>()).ShouldBeTrue();
        cases.Any(c => c.Critical && c.Expected["alert"] is not null).ShouldBeTrue();
        cases.Any(c => c.Critical && c.Expected.ContainsKey("alert") && c.Expected["alert"] is null).ShouldBeTrue();
        cases.Any(c => c.TimeZone != "UTC").ShouldBeTrue();
        cases.Any(c => c.Json["recorded_output"] is JsonValue).ShouldBeTrue();
    }

    [Fact]
    public void A_private_file_is_added_and_ids_must_be_unique()
    {
        var publicFile = Write("public.jsonl", Line("a"));
        var privateOk = Write("private-ok.jsonl", Line("b"));
        var privateDuplicate = Write("private-dup.jsonl", Line("a"));

        CaseFiles.LoadAll(publicFile, privateOk).Select(c => c.Id).ShouldBe(["a", "b"]);
        Should.Throw<InvalidOperationException>(() => CaseFiles.LoadAll(publicFile, privateDuplicate))
            .Message.ShouldContain("used more than once");
        Should.Throw<InvalidOperationException>(() => CaseFiles.LoadAll(publicFile, Path.Combine(_dir, "missing.jsonl")));
    }
}
