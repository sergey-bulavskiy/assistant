using Assistant.Application.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeRolePrompts : IRolePrompts
{
    /// <summary>Returned for roles/health/extract.md; null simulates a missing resource.</summary>
    public string? ExtractPrompt { get; set; } = "test extraction instructions";

    public IReadOnlyList<string> Missing { get; } = Array.Empty<string>();

    public string? Find(string role, string fileName) =>
        role == "health" && fileName == "extract.md" ? ExtractPrompt : null;
}
