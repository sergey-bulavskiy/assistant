using System.Globalization;

namespace Assistant.Application.Common;

// Raw config, one property per LLM_* env var, read as strings so an empty/missing value is
// distinguishable from "0" and the parser can report exactly which variable is bad.
public class LlmOptions
{
    public string ModelsRaw { get; set; } = string.Empty;
    public string CallsPerMinuteRaw { get; set; } = string.Empty;
    public string CallsPerDayRaw { get; set; } = string.Empty;
    public string MaxContextMessagesRaw { get; set; } = string.Empty;
    public string MaxInputCharsRaw { get; set; } = string.Empty;
    public string MaxOutputTokensRaw { get; set; } = string.Empty;
    public string CallTimeoutSecondsRaw { get; set; } = string.Empty;
    public string MaxConcurrentCallsRaw { get; set; } = string.Empty;
    public string ModelCooldownMinutesRaw { get; set; } = string.Empty;
}

// One "provider:name" entry from LLM_MODELS. Name (the part after the colon) is what /model and
// the catalog use to identify this entry; it must be unique across the whole catalog.
// ProviderPrefix (the part before the colon, lower-cased) is looked up by IChatClientProvider --
// Application never learns what the prefix means or which provider it names.
public record ModelCatalogEntry(string ProviderPrefix, string Name);

public class LlmConfig
{
    public required IReadOnlyList<ModelCatalogEntry> Models { get; init; }
    public required int CallsPerMinute { get; init; }
    public required int CallsPerDay { get; init; }
    public required int MaxContextMessages { get; init; }
    public required int MaxInputChars { get; init; }
    public required int MaxOutputTokens { get; init; }
    public required int CallTimeoutSeconds { get; init; }
    public required int MaxConcurrentCalls { get; init; }
    public required int ModelCooldownMinutes { get; init; }

    // M3a defines exactly one tier; its chain is LLM_MODELS' own order (spec section 3.1).
    public const string SmartTier = "smart";
}

public record LlmConfigParseResult(bool IsEnabled, LlmConfig? Config, IReadOnlyList<string> Errors);

public static class LlmConfigParser
{
    // validateProvider: given a lower-cased provider prefix, returns null when the provider is
    // usable, or an error clause (naming the missing/invalid piece of config, never its value)
    // when it is not. Keeps Application free of any provider name or credential (spec §3.1); the
    // caller (Infrastructure, at DI composition time) supplies the actual check.
    public static LlmConfigParseResult Parse(LlmOptions options, Func<string, string?> validateProvider)
    {
        if (string.IsNullOrWhiteSpace(options.ModelsRaw))
        {
            // Silent off (spec 3.2): no LLM_MODELS means the owner hasn't set up LLM yet, not a
            // misconfiguration -- no Error is logged for this case.
            return new LlmConfigParseResult(false, null, Array.Empty<string>());
        }

        // Spec §8.9: invalid ENTRIES are dropped individually (one Error each, LLM can stay on);
        // invalid LIMITS turn the WHOLE config off (one Error each). Two separate lists so an
        // entry-level problem never silently disables LLM, and a limit-level problem always does.
        var entryErrors = new List<string>();
        var limitErrors = new List<string>();

        var models = ParseModels(options.ModelsRaw, validateProvider, entryErrors);

        var callsPerMinute = ParsePositiveInt(options.CallsPerMinuteRaw, "LLM_CALLS_PER_MINUTE", limitErrors);
        var callsPerDay = ParsePositiveInt(options.CallsPerDayRaw, "LLM_CALLS_PER_DAY", limitErrors);
        var maxContextMessages = ParsePositiveInt(options.MaxContextMessagesRaw, "LLM_MAX_CONTEXT_MESSAGES", limitErrors);
        var maxInputChars = ParsePositiveInt(options.MaxInputCharsRaw, "LLM_MAX_INPUT_CHARS", limitErrors);
        var maxOutputTokens = ParsePositiveInt(options.MaxOutputTokensRaw, "LLM_MAX_OUTPUT_TOKENS", limitErrors);
        var callTimeoutSeconds = ParsePositiveInt(options.CallTimeoutSecondsRaw, "LLM_CALL_TIMEOUT_SECONDS", limitErrors);
        var maxConcurrentCalls = ParsePositiveInt(options.MaxConcurrentCallsRaw, "LLM_MAX_CONCURRENT_CALLS", limitErrors);
        var modelCooldownMinutes = ParsePositiveInt(options.ModelCooldownMinutesRaw, "LLM_MODEL_COOLDOWN_MINUTES", limitErrors);

        if (limitErrors.Count > 0)
        {
            return new LlmConfigParseResult(false, null, entryErrors.Concat(limitErrors).ToArray());
        }

        if (models.Count == 0)
        {
            // Decision (spec §8.9 doesn't explicitly cover this edge case): every configured entry
            // was individually invalid/dropped, leaving nothing to enable. Distinct from LLM_MODELS
            // being empty/unset (a silent, error-free off, handled above) -- this logs the
            // accumulated per-entry errors rather than going silent, since the owner DID try to
            // configure something and every attempt failed.
            return new LlmConfigParseResult(false, null, entryErrors);
        }

        var config = new LlmConfig
        {
            Models = models,
            CallsPerMinute = callsPerMinute,
            CallsPerDay = callsPerDay,
            MaxContextMessages = maxContextMessages,
            MaxInputChars = maxInputChars,
            MaxOutputTokens = maxOutputTokens,
            CallTimeoutSeconds = callTimeoutSeconds,
            MaxConcurrentCalls = maxConcurrentCalls,
            ModelCooldownMinutes = modelCooldownMinutes
        };
        // entryErrors may be non-empty even on success (e.g. a dropped duplicate) -- still reported
        // so the owner sees what happened, but not fatal since at least one model remains.
        return new LlmConfigParseResult(true, config, entryErrors);
    }

    private static IReadOnlyList<ModelCatalogEntry> ParseModels(string raw, Func<string, string?> validateProvider, List<string> errors)
    {
        var entries = new List<ModelCatalogEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == part.Length - 1)
            {
                errors.Add($"LLM_MODELS entry '{part}' is not in 'provider:model' form; dropped.");
                continue;
            }

            var provider = part[..colonIndex].ToLowerInvariant();
            var name = part[(colonIndex + 1)..];

            // Provider validity is checked before reserving the name for duplicate detection, so a
            // dropped entry never blocks a later, valid entry from reusing its name.
            var providerError = validateProvider(provider);
            if (providerError is not null)
            {
                errors.Add($"LLM_MODELS entry '{part}' {providerError}; dropped.");
                continue;
            }

            if (!seenNames.Add(name))
            {
                errors.Add($"LLM_MODELS entry '{part}' has a duplicate model name '{name}'; dropped (names must be unique).");
                continue;
            }

            entries.Add(new ModelCatalogEntry(provider, name));
        }

        return entries;
    }

    private static int ParsePositiveInt(string raw, string variableName, List<string> errors)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            errors.Add($"{variableName} must be a positive integer.");
            return 0;
        }

        return value;
    }
}
