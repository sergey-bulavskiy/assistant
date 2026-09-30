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

    private static readonly Regex ResetTimeRegex = new(
        @"resets\s+(?:[A-Za-z]{3}\s+)?(?<hour>\d{1,2}):(?<minute>\d{2})\s*(?<ampm>am|pm)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthFailureRegex = new(
        @"invalid api key|please run /login|authentication|\b401\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAuthorCharacters = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);

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

            if (VersionAtLeast(_options.PinnedVersion, 2, 1, 259))
            {
                arguments.Add("--permission-prompts");
                arguments.Add("none");
            }

            if (VersionAtLeast(_options.PinnedVersion, 2, 1, 248))
            {
                arguments.Add("--restricted");
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
                new ProcessRunRequest(_options.ExecutablePath, arguments, environment, tempDir, prompt, TimeSpan.FromSeconds(_options.CallTimeoutSeconds)),
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
            var roleText = message.Role == ChatRole.User ? "user" : "assistant";
            var author = SanitizeAuthor(message.AuthorName, roleText);
            var escapedText = EscapeForMsgBlock(message.Text);
            sb.Append("<msg role=\"").Append(roleText).Append("\" author=\"").Append(author).Append("\">")
              .Append(escapedText).Append("</msg>\n");
        }

        return sb.ToString();
    }

    private static string EscapeForMsgBlock(string text) =>
        text.Replace("<", "&lt;").Replace(">", "&gt;");

    private static string SanitizeAuthor(string? author, string fallbackRoleText)
    {
        var stripped = author is null ? string.Empty : NonAuthorCharacters.Replace(author, "");
        return stripped.Length > 0 ? stripped : fallbackRoleText;
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

            throw new InvalidOperationException($"claude cli reported an error for model '{modelName}' (subtype '{parsed.Subtype}').");
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, parsed.Result ?? string.Empty))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = parsed.Usage?.InputTokens,
                OutputTokenCount = parsed.Usage?.OutputTokens
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

        var kindMatch = LimitKindRegex.Match(text);
        if (!kindMatch.Success)
        {
            return false;
        }

        var kind = kindMatch.Groups["kind"].Value.ToLowerInvariant();
        scope = AccountWideLimitKinds.Contains(kind) ? LlmLimitScope.Provider : LlmLimitScope.Model;

        var timeMatch = ResetTimeRegex.Match(text);
        if (timeMatch.Success)
        {
            var hour12 = int.Parse(timeMatch.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(timeMatch.Groups["minute"].Value, CultureInfo.InvariantCulture);
            var isPm = string.Equals(timeMatch.Groups["ampm"].Value, "pm", StringComparison.OrdinalIgnoreCase);
            retryAt = ResolveNextUtcOccurrence(hour12, minute, isPm, _clock.UtcNow);
        }

        return true;
    }

    private static DateTimeOffset ResolveNextUtcOccurrence(int hour12, int minute, bool isPm, DateTimeOffset now)
    {
        var hour24 = (hour12 % 12) + (isPm ? 12 : 0);
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour24, minute, 0, TimeSpan.Zero);
        return candidate > now ? candidate : candidate.AddDays(1);
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
    }
}
