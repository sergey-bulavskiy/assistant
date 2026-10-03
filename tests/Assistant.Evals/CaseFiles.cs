using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assistant.Evals;

/// <summary>Loads and saves cases files (one JSON object per line). The public file lives in the
/// source tree (not copied to bin/), so recording rewrites the real file. A private file named by
/// EVALS_CASES_FILE is loaded in addition; it must lie outside the repository.</summary>
public static class CaseFiles
{
    public const string PrivateFileVariable = "EVALS_CASES_FILE";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The first folder above the test output folder that holds Assistant.slnx.</summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Assistant.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Assistant.slnx was not found above the test output folder.");
    }

    public static string PublicFile() => Path.Combine(RepoRoot(), "tests", "Assistant.Evals", "cases", "extraction.jsonl");

    /// <summary>The public cases plus, when EVALS_CASES_FILE is set, the private file's cases.</summary>
    public static IReadOnlyList<EvalCase> LoadAll() =>
        LoadAll(PublicFile(), Environment.GetEnvironmentVariable(PrivateFileVariable));

    public static IReadOnlyList<EvalCase> LoadAll(string publicFile, string? privateFile)
    {
        var cases = new List<EvalCase>(Load(publicFile));
        if (!string.IsNullOrWhiteSpace(privateFile))
        {
            var fullPath = Path.GetFullPath(privateFile);
            if (IsInsideAnyCheckout(fullPath))
            {
                throw new InvalidOperationException(
                    $"{PrivateFileVariable} must point to a file outside the repository: private cases are never committed.");
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"{PrivateFileVariable} points to a file that does not exist.");
            }

            cases.AddRange(Load(fullPath));
        }

        var duplicate = cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Case id '{duplicate.Key}' is used more than once.");
        }

        return cases;
    }

    /// <summary>True when the path (after following symbolic links on the file and on every parent
    /// folder) lies in a folder that holds Assistant.slnx: any checkout or worktree of this repo.</summary>
    public static bool IsInsideAnyCheckout(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var candidates = new List<string> { fullPath };
        var resolvedFile = File.Exists(fullPath) ? new FileInfo(fullPath).ResolveLinkTarget(true)?.FullName : null;
        if (resolvedFile is not null)
        {
            candidates.Add(resolvedFile);
        }

        // A drive or filesystem root (no parent) cannot be a link.
        for (var directory = Path.GetDirectoryName(fullPath); Path.GetDirectoryName(directory) is not null; directory = Path.GetDirectoryName(directory))
        {
            var target = new DirectoryInfo(directory!).ResolveLinkTarget(true)?.FullName;
            if (target is not null)
            {
                // Check the folder itself too: a link may point straight at a checkout root.
                candidates.Add(Path.Combine(target, "x"));
            }
        }

        return candidates.Any(HasSolutionAbove);
    }

    private static bool HasSolutionAbove(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Assistant.slnx")))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<EvalCase> Load(string path)
    {
        var cases = new List<EvalCase>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            try
            {
                cases.Add(EvalCase.Parse(lines[i], path));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{Path.GetFileName(path)} line {i + 1}: {ex.Message}", ex);
            }
        }

        return cases;
    }

    /// <summary>Rewrites the file with these cases, one compact line each, Cyrillic unescaped.</summary>
    public static void Save(string path, IEnumerable<EvalCase> cases)
    {
        var text = new StringBuilder();
        foreach (var evalCase in cases)
        {
            text.Append(evalCase.Json.ToJsonString(WriteOptions)).Append('\n');
        }

        // Write a temp file next to the target, then replace the target in one step, so a failed
        // write never leaves a half-written cases file.
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original error is the one that matters.
            }

            throw;
        }
    }
}
