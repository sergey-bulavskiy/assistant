namespace Assistant.Infrastructure.Llm;

/// <summary>What AddInfrastructure decided about LLM config, for Program.cs's one startup log line.
/// Errors non-empty means "on but invalid" (spec 3.2) -- logged without crashing the app; never
/// includes any config value, only variable names. Warnings are non-fatal: nothing was disabled
/// because of them (e.g. an invalid LLM_BUDGET_* with no paid entry around to ever need it).</summary>
public record LlmStartupResult(bool IsEnabled, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
