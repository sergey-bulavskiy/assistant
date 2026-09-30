using System.Globalization;

namespace Assistant.SmokeTests;

/// <summary>
/// Smoke-test settings. Read from environment variables; for local runs, KEY=VALUE lines in the
/// gitignored <c>tests/Assistant.SmokeTests/smoke.env</c> are loaded too (real environment
/// variables win).
/// </summary>
public sealed record SmokeConfig(
    int ApiId,
    string ApiHash,
    string OwnerSession,
    string ManagerToken,
    string ManagerUsername,
    string RoleBotToken,
    string RoleBotUsername,
    string GroupTitle,
    string ForumTitle,
    string? Image)
{
    public static SmokeConfig Load()
    {
        var file = LoadEnvFile();
        return Load(name =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnv ? fromEnv
            : file.TryGetValue(name, out var fromFile) && fromFile.Length > 0 ? fromFile
            : null);
    }

    public static SmokeConfig Load(Func<string, string?> lookup)
    {
        string? Optional(string name) => lookup(name) is { Length: > 0 } value ? value : null;

        string Required(string name) =>
            Optional(name) ?? throw new InvalidOperationException(
                $"Smoke test setting {name} is missing (see tests/Assistant.SmokeTests/README.md).");

        if (!int.TryParse(Required("SMOKE_TG_API_ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var apiId))
        {
            throw new InvalidOperationException("Smoke test setting SMOKE_TG_API_ID must be a number.");
        }

        return new SmokeConfig(
            apiId,
            Required("SMOKE_TG_API_HASH"),
            Required("SMOKE_OWNER_SESSION"),
            Required("SMOKE_MANAGER_BOT_TOKEN"),
            Required("SMOKE_MANAGER_BOT_USERNAME"),
            Required("SMOKE_ROLE_BOT_TOKEN"),
            Required("SMOKE_ROLE_BOT_USERNAME"),
            Required("SMOKE_GROUP_TITLE"),
            Required("SMOKE_FORUM_TITLE"),
            Optional("SMOKE_IMAGE"));
    }

    private static Dictionary<string, string> LoadEnvFile()
    {
        var values = new Dictionary<string, string>();
        var path = Path.Combine(RepoRoot.Find(), "tests", "Assistant.SmokeTests", "smoke.env");
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            var eq = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed[0] == '#' || eq <= 0)
            {
                continue;
            }

            values[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        return values;
    }
}

public static class RepoRoot
{
    /// <summary>Walks up from the test binaries to the folder holding the Dockerfile and Assistant.slnx.</summary>
    public static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile")) && File.Exists(Path.Combine(dir.FullName, "Assistant.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repo root (Dockerfile + Assistant.slnx) not found above " + AppContext.BaseDirectory);
    }
}
