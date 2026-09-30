namespace Assistant.Infrastructure.Llm;

/// <summary>What AddInfrastructure decided about LLM config, for Program.cs's one startup log line.
/// Errors non-empty means "on but invalid" (spec 3.2) -- logged without crashing the app; never
/// includes any config value, only variable names.</summary>
public record LlmStartupResult(bool IsEnabled, IReadOnlyList<string> Errors);
