using Assistant.Application.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeRolePrompts : IRolePrompts
{
    /// <summary>Returned for roles/health/extract.md; null simulates a missing resource.</summary>
    public string? ExtractPrompt { get; set; } = "test extraction instructions";

    /// <summary>Returned for roles/health/prompt.md; null simulates a missing resource.</summary>
    public string? AnswerPrompt { get; set; } = "test answer instructions";

    public IReadOnlyList<string> Missing { get; } = Array.Empty<string>();

    public string? Find(string role, string fileName) => (role, fileName) switch
    {
        ("health", "extract.md") => ExtractPrompt,
        ("health", "prompt.md") => AnswerPrompt,
        _ => null
    };
}
