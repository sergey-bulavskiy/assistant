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

    // M3b additions (spec §3, §4, §10.2, §10.4). Credentials/URLs are read as strings, same reason
    // as every other raw field here -- an empty value must be distinguishable from "not set".
    public string PricesRaw { get; set; } = string.Empty;                 // LLM_PRICES
    public string AnthropicApiKey { get; set; } = string.Empty;           // ANTHROPIC_API_KEY
    public string AnthropicProxy { get; set; } = string.Empty;            // ANTHROPIC_PROXY
    public string OpenAiApiKey { get; set; } = string.Empty;              // OPENAI_API_KEY
    public string OpenAiBaseUrl { get; set; } = string.Empty;             // OPENAI_BASE_URL
    public string OpenAiProxy { get; set; } = string.Empty;               // OPENAI_PROXY
    public string BudgetDailyUsdRaw { get; set; } = string.Empty;         // LLM_BUDGET_DAILY_USD
    public string BudgetMonthlyUsdRaw { get; set; } = string.Empty;       // LLM_BUDGET_MONTHLY_USD
    public string BudgetWarnPercentRaw { get; set; } = string.Empty;      // LLM_BUDGET_WARN_PERCENT (default 80)
    public string BudgetHardPercentRaw { get; set; } = string.Empty;      // LLM_BUDGET_HARD_PERCENT (default 120)
    public string FastModelsRaw { get; set; } = string.Empty;             // LLM_FAST_MODELS
}

// One "provider:name" entry from LLM_MODELS. Name (the part after the colon) is what /model and
// the catalog use to identify this entry; it must be unique across the whole catalog.
// ProviderPrefix (the part before the colon, lower-cased) is looked up by IChatClientProvider --
// Application never learns what the prefix means or which provider it names.
public record ModelCatalogEntry(string ProviderPrefix, string Name);

// Per-million-token USD price for one catalog entry (spec §10.2). claude-cli entries are always
// forced to 0/0 regardless of any LLM_PRICES text naming them.
public record ModelPrice(decimal InputUsdPerMillion, decimal OutputUsdPerMillion);

// Platform-wide day/month real-money budget (spec §10.4). Warn/hard are percentages of the
// relevant limit (day or month) at which the budget guard starts warning/blocking.
public record BudgetConfig(decimal DailyUsd, decimal MonthlyUsd, int WarnPercent, int HardPercent);

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
    public required IReadOnlyDictionary<string, ModelPrice> Prices { get; init; }
    public required BudgetConfig? Budget { get; init; }
    public required IReadOnlyList<ModelCatalogEntry> FastModels { get; init; }

    // M3a defines exactly one tier; its chain is LLM_MODELS' own order (spec section 3.1).
    public const string SmartTier = "smart";

    // Spec §4: LLM_FAST_MODELS is the "fast" tier, resolved against the surviving LLM_MODELS catalog.
    public const string FastTier = "fast";
}

public record LlmConfigParseResult(bool IsEnabled, LlmConfig? Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

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
            return new LlmConfigParseResult(false, null, Array.Empty<string>(), Array.Empty<string>());
        }

        // Spec §8.9: invalid ENTRIES are dropped individually (one Error each, LLM can stay on);
        // invalid LIMITS turn the WHOLE config off (one Error each). Two separate lists so an
        // entry-level problem never silently disables LLM, and a limit-level problem always does.
        var entryErrors = new List<string>();
        var limitErrors = new List<string>();
        var warnings = new List<string>();

        var callsPerMinute = ParsePositiveInt(options.CallsPerMinuteRaw, "LLM_CALLS_PER_MINUTE", limitErrors);
        var callsPerDay = ParsePositiveInt(options.CallsPerDayRaw, "LLM_CALLS_PER_DAY", limitErrors);
        var maxContextMessages = ParsePositiveInt(options.MaxContextMessagesRaw, "LLM_MAX_CONTEXT_MESSAGES", limitErrors);
        var maxInputChars = ParsePositiveInt(options.MaxInputCharsRaw, "LLM_MAX_INPUT_CHARS", limitErrors);
        var maxOutputTokens = ParsePositiveInt(options.MaxOutputTokensRaw, "LLM_MAX_OUTPUT_TOKENS", limitErrors);
        var callTimeoutSeconds = ParsePositiveInt(options.CallTimeoutSecondsRaw, "LLM_CALL_TIMEOUT_SECONDS", limitErrors);
        var maxConcurrentCalls = ParsePositiveInt(options.MaxConcurrentCallsRaw, "LLM_MAX_CONCURRENT_CALLS", limitErrors);
        var modelCooldownMinutes = ParsePositiveInt(options.ModelCooldownMinutesRaw, "LLM_MODEL_COOLDOWN_MINUTES", limitErrors);

        var (budget, budgetProblem) = ParseBudget(options);

        // Review nit: an invalid (not merely absent) LLM_BUDGET_* with no anthropic:/openai: entry
        // even attempted in LLM_MODELS would otherwise go completely unlogged -- a paid entry
        // reports budgetProblem itself (one Error per dropped entry, below), but with no paid entry
        // to drop there is no other place this ever surfaces. One Warning (not Error: nothing was
        // actually disabled by it -- claude-cli entries never need a budget).
        if (budgetProblem is not null && BudgetWasAttempted(options) && !HasAnyPaidProviderEntry(options.ModelsRaw))
        {
            warnings.Add(budgetProblem);
        }

        if (limitErrors.Count > 0)
        {
            return new LlmConfigParseResult(false, null, entryErrors.Concat(limitErrors).ToArray(), warnings);
        }

        var prices = ParsePrices(options.PricesRaw, entryErrors);

        var models = ParseModels(options, validateProvider, prices, budget, budgetProblem, entryErrors);

        // Spec: claude-cli entries are priced 0 regardless of any LLM_PRICES text naming them.
        foreach (var entry in models.Where(m => string.Equals(m.ProviderPrefix, LlmProviderClaudeCliPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            prices[entry.Name] = new ModelPrice(0m, 0m);
        }

        var fastModels = ParseFastModels(options.FastModelsRaw, models, entryErrors);

        if (models.Count == 0)
        {
            // Decision (spec §8.9 doesn't explicitly cover this edge case): every configured entry
            // was individually invalid/dropped, leaving nothing to enable. Distinct from LLM_MODELS
            // being empty/unset (a silent, error-free off, handled above) -- this logs the
            // accumulated per-entry errors rather than going silent, since the owner DID try to
            // configure something and every attempt failed.
            return new LlmConfigParseResult(false, null, entryErrors, warnings);
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
            ModelCooldownMinutes = modelCooldownMinutes,
            Prices = prices,
            Budget = budget,
            FastModels = fastModels
        };
        // entryErrors may be non-empty even on success (e.g. a dropped duplicate) -- still reported
        // so the owner sees what happened, but not fatal since at least one model remains.
        return new LlmConfigParseResult(true, config, entryErrors, warnings);
    }

    private static bool BudgetWasAttempted(LlmOptions options) =>
        !string.IsNullOrWhiteSpace(options.BudgetDailyUsdRaw) || !string.IsNullOrWhiteSpace(options.BudgetMonthlyUsdRaw);

    private static bool HasAnyPaidProviderEntry(string modelsRaw)
    {
        foreach (var part in modelsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == part.Length - 1)
            {
                continue;
            }

            var provider = part[..colonIndex];
            if (PaidProviderPrefixes.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Application never learns what a provider prefix means (spec §3.1) except for this one
    // literal, needed only to force claude-cli's own price to 0/0 -- it never affects which
    // providers' entries are kept, only pricing of an already-kept claude-cli entry.
    private const string LlmProviderClaudeCliPrefix = "claude-cli";

    private static readonly string[] PaidProviderPrefixes = { "anthropic", "openai" };

    private static IReadOnlyList<ModelCatalogEntry> ParseModels(
        LlmOptions options, Func<string, string?> validateProvider,
        IReadOnlyDictionary<string, ModelPrice> prices, BudgetConfig? budget, string? budgetProblem, List<string> errors)
    {
        var entries = new List<ModelCatalogEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in options.ModelsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == part.Length - 1)
            {
                errors.Add($"LLM_MODELS entry '{part}' is not in 'provider:model' form; dropped.");
                continue;
            }

            var provider = part[..colonIndex].ToLowerInvariant();
            var name = part[(colonIndex + 1)..];

            // Security review nit: a model name starting with '-' would be passed to the CLI as
            // `--model <name>` and could be misread as another flag (e.g. an injected option)
            // rather than a model argument. Reject it the same way as any other malformed entry.
            if (name.StartsWith('-'))
            {
                errors.Add($"LLM_MODELS entry '{part}' has a model name starting with '-'; dropped.");
                continue;
            }

            // Provider validity is checked before reserving the name for duplicate detection, so a
            // dropped entry never blocks a later, valid entry from reusing its name. This callback
            // only tells us whether Infrastructure recognizes/can use this provider prefix at all
            // (e.g. claude-cli needing a token) -- price/budget are M3b additions this parser checks
            // itself below, since Application never learns a provider's credentials either way.
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

            if (PaidProviderPrefixes.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                var isAnthropic = string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);
                var apiKey = isAnthropic ? options.AnthropicApiKey : options.OpenAiApiKey;
                var keyVar = isAnthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";

                if (string.IsNullOrEmpty(apiKey))
                {
                    errors.Add($"LLM_MODELS entry '{part}' needs a non-empty {keyVar}; dropped.");
                    seenNames.Remove(name);
                    continue;
                }

                if (!prices.ContainsKey(name))
                {
                    errors.Add($"LLM_MODELS entry '{part}' has no price in LLM_PRICES; dropped.");
                    seenNames.Remove(name);
                    continue;
                }

                if (budget is null)
                {
                    errors.Add($"LLM_MODELS entry '{part}' dropped: {budgetProblem}.");
                    seenNames.Remove(name);
                    continue;
                }
            }

            entries.Add(new ModelCatalogEntry(provider, name));
        }

        return entries;
    }

    private static Dictionary<string, ModelPrice> ParsePrices(string raw, List<string> errors)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
            {
                errors.Add($"LLM_PRICES entry '{part}' is not in 'name=input/output' form; dropped.");
                continue;
            }

            var name = part[..eq];
            var priceText = part[(eq + 1)..];
            var slash = priceText.IndexOf('/');
            if (slash <= 0 || slash == priceText.Length - 1)
            {
                errors.Add($"LLM_PRICES entry '{part}' price is not in 'input/output' form; dropped.");
                continue;
            }

            var inputOk = decimal.TryParse(priceText[..slash], NumberStyles.Number, CultureInfo.InvariantCulture, out var input) && input >= 0;
            var outputOk = decimal.TryParse(priceText[(slash + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture, out var output) && output >= 0;
            if (!inputOk || !outputOk)
            {
                errors.Add($"LLM_PRICES entry '{part}' has a non-numeric or negative price; dropped.");
                continue;
            }

            if (!prices.TryAdd(name, new ModelPrice(input, output)))
            {
                errors.Add($"LLM_PRICES entry '{part}' has a duplicate name '{name}'; dropped (keeping the first).");
            }
        }

        return prices;
    }

    private static (BudgetConfig? Budget, string? Problem) ParseBudget(LlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BudgetDailyUsdRaw) && string.IsNullOrWhiteSpace(options.BudgetMonthlyUsdRaw))
        {
            return (null, "LLM_BUDGET_DAILY_USD/LLM_BUDGET_MONTHLY_USD are not set");
        }

        if (!decimal.TryParse(options.BudgetDailyUsdRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var daily) || daily <= 0)
        {
            return (null, "LLM_BUDGET_DAILY_USD must be a positive number");
        }

        if (!decimal.TryParse(options.BudgetMonthlyUsdRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var monthly) || monthly <= 0)
        {
            return (null, "LLM_BUDGET_MONTHLY_USD must be a positive number");
        }

        if (daily > monthly)
        {
            return (null, "LLM_BUDGET_DAILY_USD must not exceed LLM_BUDGET_MONTHLY_USD");
        }

        var warnRaw = string.IsNullOrWhiteSpace(options.BudgetWarnPercentRaw) ? "80" : options.BudgetWarnPercentRaw;
        var hardRaw = string.IsNullOrWhiteSpace(options.BudgetHardPercentRaw) ? "120" : options.BudgetHardPercentRaw;

        if (!int.TryParse(warnRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warn) || warn <= 0 || warn >= 100)
        {
            return (null, "LLM_BUDGET_WARN_PERCENT must be a positive integer below 100");
        }

        if (!int.TryParse(hardRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hard) || hard < 100)
        {
            return (null, "LLM_BUDGET_HARD_PERCENT must be an integer of at least 100");
        }

        return (new BudgetConfig(daily, monthly, warn, hard), null);
    }

    private static IReadOnlyList<ModelCatalogEntry> ParseFastModels(string raw, IReadOnlyList<ModelCatalogEntry> models, List<string> errors)
    {
        var result = new List<ModelCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == part.Length - 1)
            {
                errors.Add($"LLM_FAST_MODELS entry '{part}' is not in 'provider:model' form; dropped.");
                continue;
            }

            var provider = part[..colonIndex];
            var name = part[(colonIndex + 1)..];

            if (!seen.Add(name))
            {
                errors.Add($"LLM_FAST_MODELS entry '{part}' is a duplicate; dropped.");
                continue;
            }

            var match = models.FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.ProviderPrefix, provider, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                errors.Add($"LLM_FAST_MODELS entry '{part}' does not match a configured LLM_MODELS entry; dropped.");
                continue;
            }

            result.Add(match);
        }

        return result;
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
