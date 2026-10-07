using System.Reflection;
using System.Text;
using Assistant.Application.Llm;

namespace Assistant.Infrastructure.Roles;

/// <summary>Reads every "roles/..." manifest resource of the given assembly once, at construction.
/// Registered as a singleton over this assembly.</summary>
public sealed class RolePrompts : IRolePrompts
{
    public static readonly IReadOnlyList<string> RequiredResources = new[]
    {
        "roles/health/prompt.md",
        "roles/health/extract.md",
        "roles/vet/prompt.md",
        "roles/vet/extract.md"
    };

    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public RolePrompts(Assembly assembly)
    {
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            // LogicalName uses %(RecursiveDir): "roles/health\prompt.md" on Windows, "roles/health/prompt.md" elsewhere.
            var name = resourceName.Replace('\\', '/');
            if (!name.StartsWith("roles/", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            _texts[name] = reader.ReadToEnd();
        }

        Missing = RequiredResources.Where(r => !_texts.TryGetValue(r, out var text) || string.IsNullOrWhiteSpace(text)).ToArray();
    }

    public IReadOnlyList<string> Missing { get; }

    public string? Find(string role, string fileName) =>
        _texts.TryGetValue($"roles/{role.Trim()}/{fileName}", out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
