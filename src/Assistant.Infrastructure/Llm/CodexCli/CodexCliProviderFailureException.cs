namespace Assistant.Infrastructure.Llm.CodexCli;

/// <summary>Only fixed operational categories may cross the CLI output boundary.</summary>
public sealed class CodexCliProviderFailureException : InvalidOperationException
{
    public string Category { get; }

    public CodexCliProviderFailureException(string category)
        : base($"codex-cli unavailable ({Normalize(category)}).")
    {
        Category = Normalize(category);
    }

    private static string Normalize(string category) => category switch
    {
        "configuration" or "model" or "unsupported-content" or "unsupported-image"
        or "output-limit" or "version" or "catalog" or "model-not-in-pinned-catalog"
        or "model-image-capability" or "final-output" or "unexpected-tool-output"
        or "usage" or "unexpected-event" or "invalid-json" or "authentication"
        or "incomplete-result" or "process" => category,
        _ => "process"
    };
}
