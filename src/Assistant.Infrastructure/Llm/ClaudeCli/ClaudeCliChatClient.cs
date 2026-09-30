using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Assistant.Application.Common;
using Assistant.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Llm.ClaudeCli;

/// <summary>Runs the Claude Code CLI in print mode, one process per call, with every tool and MCP
/// server disabled and no session state persisted (see this file's AGENTS.md pitfalls section
/// and the plan's "Verified facts"/"Decisions made by the plan" for why each flag below is there).
/// stdout/stderr/prompt/answer are never logged -- only model name, exit code, duration and
/// exception type (an auth failure additionally logs one fixed marker line, never the CLI's own
/// text).</summary>
public class ClaudeCliChatClient : IChatClient
{
    private static readonly string[] AccountWideLimitKinds = { "session", "weekly", "spend", "usage" };

    private static readonly Regex LimitKindRegex = new(
        @"your\s+(?<kind>[a-zA-Z]+)\s+limit", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Finding S4: monthly/individual/org spend limits and a "shared budget" don't fit the "your X
    // limit" shape above (there's more than one word between "your" and "limit", or no "your" at
    // all) but are account-wide exactly like session/weekly/spend/usage, and never carry a reset
    // time.
    private static readonly Regex SpendOrBudgetRegex = new(
        @"spend limit|shared budget", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Review should-fix #3: other account-wide phrasings that don't fit the "your X limit" shape
    // above -- "You've hit your limit" (no word between "your" and "limit"), "usage limit" (no
    // "your" at all) and "N-hour limit" (e.g. "5-hour limit reached"). Unlike spend/budget these DO
    // carry a reset time, so (unlike SpendOrBudgetRegex) matching this still falls through to the
    // reset-time parsing below.
    private static readonly Regex GenericAccountLimitRegex = new(
        @"hit your limit|usage limit|\d+-hour limit", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Finding S6: the weekday (e.g. "Mon") is captured, not just skipped, so a limit message like
    // "...weekly limit · resets Mon 12:00am" resolves against that specific weekday rather than
    // "the next occurrence of this clock time", which could be the wrong day. Review should-fix #3:
    // the minute is optional ("resets 3pm" carries no minute at all -- treated as :00).
    private static readonly Regex ResetTimeRegex = new(
        @"resets\s+(?:(?<weekday>[A-Za-z]{3})\s+)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Finding S5.
    private static readonly Regex AuthFailureRegex = new(
        @"authenticat|/login|oauth token|login (expired|was rejected)|invalid api key|\b401\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAuthorCharacters = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);

    // Nit: an exception message may include the CLI's own `subtype` value, but only when it looks
    // like the small fixed vocabulary the CLI actually uses (e.g. "error_max_turns") -- never
    // free-form text that could smuggle unexpected data into a log/exception message.
    private static readonly Regex SafeSubtypePattern = new(@"^[a-z_]+$", RegexOptions.Compiled);

    private readonly IProcessRunner _runner;
    private readonly ClaudeCliOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<ClaudeCliChatClient> _logger;

    public ClaudeCliChatClient(IProcessRunner runner, ClaudeCliOptions options, IClock clock, ILogger<ClaudeCliChatClient> logger)
    {
        _runner = runner;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var modelName = options?.ModelId ?? throw new InvalidOperationException("ChatOptions.ModelId is required (the catalog model name).");
        var maxOutputTokens = options?.MaxOutputTokens ?? _options.MaxOutputTokens;

        // Finding S3: refuse to run at all with a HOME the CLI would be pointed at that isn't a
        // real, absolute directory -- a relative or empty value could resolve against whatever the
        // current working directory happens to be, defeating the point of a dedicated $CLAUDE_HOME.
        if (string.IsNullOrWhiteSpace(_options.HomeDirectory) || !Path.IsPathRooted(_options.HomeDirectory))
        {
            // Nit: one distinct Error line naming the reason category, never any configured value.
            _logger.LogError("claude-cli refused to run: home directory not an absolute path");
            throw new InvalidOperationException("claude cli invocation refused: HomeDirectory is not configured as an absolute path.");
        }

        // Finding B1: `--restricted` must ALWAYS be sent -- never silently skipped because the
        // pinned version is empty/unparsable/too old. Refuse the call instead of running the CLI
        // without it.
        if (!VersionAtLeast(_options.PinnedVersion, 2, 1, 248))
        {
            _logger.LogError("claude-cli refused to run: pinned version does not support restricted mode");
            throw new InvalidOperationException("claude cli invocation refused: the pinned CLI version does not support required restricted mode.");
        }

        var messageList = messages.ToList();
        var systemPrompt = messageList.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
        var prompt = RenderPrompt(messageList);

        var tempDir = Directory.CreateTempSubdirectory("assistant-llm-").FullName;
        try
        {
            var systemPromptFile = Path.Combine(tempDir, "system-prompt.txt");
            await File.WriteAllTextAsync(systemPromptFile, systemPrompt, cancellationToken);

            // Spec §8.5's exact flag list. Deliberately NOT included: --bare (ignores
            // CLAUDE_CODE_OAUTH_TOKEN and keeps tools available -- see Decision on this), --mcp-config
            // (no MCP servers are configured at all; --strict-mcp-config alone means "only servers
            // from --mcp-config", and omitting that flag leaves the set empty), --setting-sources /
            // --input-format (not in the spec's verified list -- see the header Decision flagging the
            // remaining settings-file exposure this implies).
            var arguments = new List<string>
            {
                "-p",
                "--output-format", "json",
                "--model", modelName,
                "--tools", "",
                "--disallowedTools", "mcp__*",
                "--strict-mcp-config",
                "--disable-slash-commands",
                "--no-session-persistence",
                "--max-turns", "1",
                "--system-prompt-file", systemPromptFile
            };

            // Already refused above when this threshold isn't met -- always sent from here on.
            arguments.Add("--restricted");

            if (VersionAtLeast(_options.PinnedVersion, 2, 1, 259))
            {
                arguments.Add("--permission-prompts");
                arguments.Add("none");
            }

            // Environment is an EXACT allowlist (spec §8.6): never the bot tokens, encryption key or
            // connection string, and never ANTHROPIC_API_KEY (it would outrank the OAuth token and
            // bill the API outside all budgets).
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                ["HOME"] = _options.HomeDirectory,
                ["CLAUDE_CODE_OAUTH_TOKEN"] = _options.OAuthToken,
                ["DISABLE_AUTOUPDATER"] = "1",
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
                ["TZ"] = "UTC",
                ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maxOutputTokens.ToString(CultureInfo.InvariantCulture)
            };

            var runResult = await _runner.RunAsync(
                new ProcessRunRequest(ResolveExecutablePath(), arguments, environment, tempDir, prompt, TimeSpan.FromSeconds(_options.CallTimeoutSeconds)),
                cancellationToken);

            _logger.LogInformation(
                "claude cli call for model {Model} exited {ExitCode} (timed out: {TimedOut})",
                modelName, runResult.ExitCode, runResult.TimedOut);

            if (runResult.TimedOut)
            {
                throw new TimeoutException($"claude cli call for model '{modelName}' timed out after {_options.CallTimeoutSeconds}s.");
            }

            return ParseResult(runResult, modelName);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup -- never let a leftover temp dir fail the call.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above -- e.g. a file still briefly locked by the just-exited child.
            }
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ClaudeCliChatClient is called through LlmGateway.CompleteAsync only; streaming is not used in M3a.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    /// <summary>True when <paramref name="pinnedVersion"/> (CLAUDE_CLI_VERSION, e.g. "2.1.285") is
    /// >= major.minor.patch. An unparsable pinned version is treated as NOT meeting the threshold
    /// (fails closed -- never sends a flag an older/unrecognised CLI build might reject).</summary>
    private static bool VersionAtLeast(string pinnedVersion, int major, int minor, int patch) =>
        Version.TryParse(pinnedVersion, out var parsed) && parsed >= new Version(major, minor, patch);

    /// <summary>Nit: when <see cref="ClaudeCliOptions.ExecutablePath"/> was left at its
    /// "not configured" sentinel, resolve the plan's verified install path
    /// (`$HomeDirectory/.local/bin/claude`) instead of a bare `claude`, which would depend on PATH
    /// resolution order in the child's cleaned environment. An explicitly configured
    /// ExecutablePath is always used as-is.</summary>
    private string ResolveExecutablePath() =>
        _options.ExecutablePath ?? _options.HomeDirectory.TrimEnd('/') + "/.local/bin/claude";

    /// <summary>Renders the conversation as spec §8.4's delimited blocks:
    /// `&lt;msg role="user|assistant" author="..."&gt;...&lt;/msg&gt;`. `&lt;`/`&gt;` inside the text
    /// are escaped so a message can never be mistaken for a block boundary; the author is reduced to
    /// `[A-Za-z0-9_]` (falling back to the role name if that strips everything). The system message is
    /// skipped here -- it goes to --system-prompt-file, never onto stdin.</summary>
    private static string RenderPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages.Where(m => m.Role != ChatRole.System))
        {
            // Nit: only user/assistant turns are ever legitimate here (the system message is
            // filtered out above and goes to --system-prompt-file instead); anything else (e.g. a
            // Tool role) would have no correct rendering and must not be silently folded into
            // "assistant".
            string roleText;
            if (message.Role == ChatRole.User)
            {
                roleText = "user";
            }
            else if (message.Role == ChatRole.Assistant)
            {
                roleText = "assistant";
            }
            else
            {
                throw new InvalidOperationException($"ClaudeCliChatClient cannot render a message with role '{message.Role}'.");
            }

            var author = SanitizeAuthor(message.AuthorName, roleText);
            var escapedText = EscapeForMsgBlock(message.Text);
            sb.Append("<msg role=\"").Append(roleText).Append("\" author=\"").Append(author).Append("\">")
              .Append(escapedText).Append("</msg>\n");
        }

        return sb.ToString();
    }

    // Nit: `&` is escaped FIRST, then `<`/`>` -- escaping in the other order would turn a literal
    // "&lt;" the user typed into "&amp;lt;" becoming "&lt;" again after the model reads it back, but
    // worse, escaping `<`/`>` first and `&` after would double-escape the entities THIS method just
    // produced (e.g. "&lt;" -> "&amp;lt;"), corrupting the delimiter escaping itself.
    private static string EscapeForMsgBlock(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string SanitizeAuthor(string? author, string fallbackRoleText)
    {
        var stripped = author is null ? string.Empty : NonAuthorCharacters.Replace(author, "");
        // Nit: prefixed with "u_" so a Telegram username that happens to be literally "system" or
        // "assistant" can never render as author="system"/"assistant" and be mistaken for something
        // other than an ordinary conversation participant.
        return "u_" + (stripped.Length > 0 ? stripped : fallbackRoleText);
    }

    private ChatResponse ParseResult(ProcessRunResult result, string modelName)
    {
        CliJsonResult? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CliJsonResult>(result.StandardOutput);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            // Nit: a non-zero exit with no JSON at all (the CLI crashed/refused before ever printing
            // its --output-format json result) may still be an auth failure -- check stderr for the
            // same marker, but never log its text (only the fixed Error line, same as the JSON path).
            if (result.ExitCode != 0 && AuthFailureRegex.IsMatch(result.StandardError))
            {
                _logger.LogError("claude-cli authentication failed");
                throw new InvalidOperationException($"claude cli authentication failed for model '{modelName}'.");
            }

            throw new InvalidOperationException($"claude cli produced no parseable JSON result for model '{modelName}' (exit code {result.ExitCode}).");
        }

        if (parsed.IsError)
        {
            if (TryParseLimitMessage(parsed.Result, out var scope, out var retryAt))
            {
                throw new ModelLimitReachedException($"claude cli reported a usage/rate limit for model '{modelName}'.", scope, retryAt);
            }

            if (parsed.Result is not null && AuthFailureRegex.IsMatch(parsed.Result))
            {
                // Spec §8.7: a distinct Error line, never the CLI's own text (security requirement).
                _logger.LogError("claude-cli authentication failed");
                throw new InvalidOperationException($"claude cli authentication failed for model '{modelName}'.");
            }

            // Nit: the CLI's `subtype` is only ever included in the message when it matches the
            // small fixed vocabulary the CLI actually uses -- never arbitrary text.
            var subtype = parsed.Subtype is { } s && SafeSubtypePattern.IsMatch(s) ? $" (subtype '{s}')" : string.Empty;
            throw new InvalidOperationException($"claude cli reported an error for model '{modelName}'{subtype}.");
        }

        // Finding S7: cache_read_input_tokens and cache_creation_input_tokens both count toward the
        // billed input for this call, so both fold into InputTokenCount; cache_read_input_tokens
        // specifically is also exposed as CachedInputTokenCount (tokens served from cache rather
        // than freshly processed). TotalTokenCount is input + output, set explicitly since it is not
        // computed automatically by UsageDetails.
        var inputTokens = SumNullable(parsed.Usage?.InputTokens, parsed.Usage?.CacheReadInputTokens, parsed.Usage?.CacheCreationInputTokens);
        var outputTokens = parsed.Usage?.OutputTokens;
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, parsed.Result ?? string.Empty))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                CachedInputTokenCount = parsed.Usage?.CacheReadInputTokens,
                TotalTokenCount = SumNullable(inputTokens, outputTokens)
            }
        };

        if (parsed.TotalCostUsd is { } cost)
        {
            response.AdditionalProperties = new AdditionalPropertiesDictionary { [LlmResponseKeys.ReportedCostUsd] = (decimal)cost };
        }

        return response;
    }

    /// <summary>Spec §8.7: CLI limit messages read like "You've hit your session limit · resets
    /// 3:45pm" / "...weekly limit..." / "...Opus limit..." (local time, no date; the child runs with
    /// TZ=UTC, so "next occurrence of that clock time in UTC" is the resolution rule).
    /// Session/weekly/spend/usage are account-wide (Provider scope, marks every claude-cli catalog
    /// entry unavailable); anything else named ("Opus limit", "Sonnet limit", ...) is Model scope
    /// (marks only that entry). An unparsable reset time still returns true with
    /// <paramref name="retryAt"/> null -- the gateway then falls back to
    /// LLM_MODEL_COOLDOWN_MINUTES (spec: "Unparsable reset time: cooldown").</summary>
    private bool TryParseLimitMessage(string? text, out LlmLimitScope scope, out DateTimeOffset? retryAt)
    {
        scope = LlmLimitScope.Provider;
        retryAt = null;

        if (text is null)
        {
            return false;
        }

        // Finding S4: spend/budget limits are account-wide and never carry a reset time -- handled
        // separately from the "your X limit" shape below, which they don't match.
        if (SpendOrBudgetRegex.IsMatch(text))
        {
            scope = LlmLimitScope.Provider;
            retryAt = null;
            return true;
        }

        var kindMatch = LimitKindRegex.Match(text);
        if (kindMatch.Success)
        {
            var kind = kindMatch.Groups["kind"].Value.ToLowerInvariant();
            scope = AccountWideLimitKinds.Contains(kind) ? LlmLimitScope.Provider : LlmLimitScope.Model;
        }
        else if (GenericAccountLimitRegex.IsMatch(text))
        {
            scope = LlmLimitScope.Provider;
        }
        else
        {
            return false;
        }

        var timeMatch = ResetTimeRegex.Match(text);
        if (timeMatch.Success)
        {
            // Finding S6: no exception may escape parsing -- any unexpected shape just leaves
            // retryAt null (the gateway then falls back to the configured cooldown) rather than
            // throwing out of what is otherwise a successfully recognised limit message.
            try
            {
                retryAt = TryResolveResetTime(timeMatch, _clock.UtcNow);
            }
            catch (Exception)
            {
                retryAt = null;
            }
        }

        return true;
    }

    /// <summary>Finding S6: resolves "resets 3:45pm" / "resets Mon 12:00am" style reset times to the
    /// next matching UTC instant, or null for anything that doesn't parse as a valid clock time
    /// (hour 1-12, minute 0-59) or a recognised weekday abbreviation. Never throws.</summary>
    private static DateTimeOffset? TryResolveResetTime(Match timeMatch, DateTimeOffset now)
    {
        if (!int.TryParse(timeMatch.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour12) ||
            hour12 < 1 || hour12 > 12)
        {
            return null;
        }

        // Review should-fix #3: the minute group is optional ("resets 3pm" carries none) -- absent
        // means :00, present but unparsable/out-of-range still fails the whole match as before.
        var minuteGroup = timeMatch.Groups["minute"];
        int minute;
        if (!minuteGroup.Success)
        {
            minute = 0;
        }
        else if (!int.TryParse(minuteGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out minute) ||
            minute < 0 || minute > 59)
        {
            return null;
        }

        var isPm = string.Equals(timeMatch.Groups["ampm"].Value, "pm", StringComparison.OrdinalIgnoreCase);
        var hour24 = (hour12 % 12) + (isPm ? 12 : 0);

        var weekdayGroup = timeMatch.Groups["weekday"];
        if (!weekdayGroup.Success)
        {
            return ResolveNextUtcOccurrence(hour24, minute, now);
        }

        return TryParseWeekday(weekdayGroup.Value, out var targetDayOfWeek)
            ? ResolveNextUtcWeekdayOccurrence(targetDayOfWeek, hour24, minute, now)
            : null;
    }

    private static bool TryParseWeekday(string text, out DayOfWeek dayOfWeek)
    {
        switch (text.ToLowerInvariant())
        {
            case "mon": dayOfWeek = DayOfWeek.Monday; return true;
            case "tue": dayOfWeek = DayOfWeek.Tuesday; return true;
            case "wed": dayOfWeek = DayOfWeek.Wednesday; return true;
            case "thu": dayOfWeek = DayOfWeek.Thursday; return true;
            case "fri": dayOfWeek = DayOfWeek.Friday; return true;
            case "sat": dayOfWeek = DayOfWeek.Saturday; return true;
            case "sun": dayOfWeek = DayOfWeek.Sunday; return true;
            default:
                dayOfWeek = default;
                return false;
        }
    }

    private static DateTimeOffset ResolveNextUtcOccurrence(int hour24, int minute, DateTimeOffset now)
    {
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour24, minute, 0, TimeSpan.Zero);
        return candidate > now ? candidate : candidate.AddDays(1);
    }

    /// <summary>Next occurrence of <paramref name="targetDayOfWeek"/> at the given UTC clock time: if
    /// today IS that weekday and the time is still later today, today's occurrence is used;
    /// otherwise (today is a different weekday, or today IS the weekday but that time already
    /// passed) the next week's occurrence is used.</summary>
    private static DateTimeOffset ResolveNextUtcWeekdayOccurrence(DayOfWeek targetDayOfWeek, int hour24, int minute, DateTimeOffset now)
    {
        var daysUntil = ((int)targetDayOfWeek - (int)now.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour24, minute, 0, TimeSpan.Zero).AddDays(daysUntil);
        return candidate > now ? candidate : candidate.AddDays(7);
    }

    private sealed class CliJsonResult
    {
        [JsonPropertyName("is_error")]
        public bool IsError { get; set; }

        [JsonPropertyName("subtype")]
        public string? Subtype { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("total_cost_usd")]
        public double? TotalCostUsd { get; set; }

        [JsonPropertyName("usage")]
        public CliUsage? Usage { get; set; }
    }

    private sealed class CliUsage
    {
        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
        public int? CacheReadInputTokens { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public int? CacheCreationInputTokens { get; set; }
    }

    /// <summary>Adds nullable token counts, treating a null entry as "not reported" (0) rather than
    /// poisoning the whole sum -- but returns null itself when every entry is null, so a response
    /// with no usage info at all still reports no usage rather than 0.</summary>
    private static int? SumNullable(params int?[] values)
    {
        int? sum = null;
        foreach (var value in values)
        {
            if (value is { } v)
            {
                sum = (sum ?? 0) + v;
            }
        }

        return sum;
    }
}
