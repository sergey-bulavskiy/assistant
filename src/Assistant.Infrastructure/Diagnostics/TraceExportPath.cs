using System.Security;

namespace Assistant.Infrastructure.Diagnostics;

public static class TraceExportPath
{
    public static bool TryResolve(string? candidate, out string? absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        try
        {
            var path = Path.GetFullPath(candidate);
            if (File.Exists(path) || Directory.Exists(path) || IsInsideGitRepository(path)) return false;
            absolutePath = path;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException) { return false; }
    }

    private static bool IsInsideGitRepository(string path)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (directory is not null)
        {
            if (HasGitAncestor(directory, inspected)) return true;
            if (directory.Exists && directory.LinkTarget is not null)
            {
                var resolved = directory.ResolveLinkTarget(true);
                if (resolved is DirectoryInfo real && HasGitAncestor(real, inspected)) return true;
            }
            directory = directory.Parent;
        }
        return false;
    }

    private static bool HasGitAncestor(DirectoryInfo? directory, HashSet<string> inspected)
    {
        while (directory is not null && inspected.Add(directory.FullName))
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return true;
            directory = directory.Parent;
        }
        return false;
    }
}
