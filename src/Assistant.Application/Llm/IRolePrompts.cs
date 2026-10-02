namespace Assistant.Application.Llm;

/// <summary>Role prompt files (repo-root roles/&lt;role&gt;/*.md, embedded at build time).</summary>
public interface IRolePrompts
{
    /// <summary>Text of roles/&lt;role&gt;/&lt;fileName&gt;, or null when it is missing or empty. A caller
    /// that gets null turns that LLM feature off (never a crash).</summary>
    string? Find(string role, string fileName);

    /// <summary>Required resource names that are missing or empty; logged as Errors at startup.</summary>
    IReadOnlyList<string> Missing { get; }
}
