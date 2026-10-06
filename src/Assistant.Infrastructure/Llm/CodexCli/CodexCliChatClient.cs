using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application.Common;
using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm.CodexCli;

/// <summary>Text-only ChatGPT subscription execution. Raw process output is never logged or
/// attached to a response; each call replaces model instructions and disables local capabilities.</summary>
public sealed class CodexCliChatClient(IProcessRunner runner, CodexCliOptions settings) : IChatClient
{
    private static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apps", "plugins", "remote_plugin", "browser_use",
        "computer_use", "image_generation", "view_image", "sleep_tool", "multi_agent", "goals",
        "memories", "hooks", "skill_search", "skill_mcp_dependency_install", "tool_suggest",
        "code_mode_host", "default_mode_request_user_input", "send_message_to_user_async",
        "current_time_reminder", "token_budget", "deferred_executor"
    ];

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(settings.ExecutablePath) || !Path.IsPathRooted(settings.HomeDirectory))
            throw Failure("configuration");
        var model = options?.ModelId;
        if (string.IsNullOrWhiteSpace(model) || model.StartsWith('-')) throw Failure("model");
        var turns = messages.ToArray();
        if (turns.Any(m => m.Role != ChatRole.System && m.Role != ChatRole.User && m.Role != ChatRole.Assistant
            || m.Contents.Any(c => c is not TextContent))) throw Failure("unsupported-content");
        var outputLimit = options?.MaxOutputTokens ?? settings.MaxOutputTokens;
        if (outputLimit <= 0 || outputLimit > 100000) throw Failure("output-limit");
        if (settings.CallTimeoutSeconds <= 0) throw Failure("configuration");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.CallTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var callToken = linked.Token;
        var root = Directory.CreateTempSubdirectory("assistant-codex-").FullName;
        try
        {
            var environment = CreateEnvironment(root);
            // Inspect the immutable binary without loading the subscription home's configuration.
            var inspectionEnvironment = new Dictionary<string, string>(environment) { ["CODEX_HOME"] = root };
            var version = await Run(["--version"], inspectionEnvironment, root, string.Empty, callToken);
            if (version.ExitCode != 0 || version.StandardOutput.Trim() != $"codex-cli {CodexCliOptions.PinnedVersion}")
                throw Failure("version");
            var bundled = await Run(["debug", "models", "--bundled"], inspectionEnvironment, root, string.Empty, callToken);
            if (bundled.ExitCode != 0) throw Failure("catalog");
            var catalog = PrepareCatalog(bundled.StandardOutput, model);
            var catalogFile = Path.Combine(root, "models.json");
            await File.WriteAllTextAsync(catalogFile, catalog.ToJsonString(), callToken);
            var instructionsFile = Path.Combine(root, "instructions.txt");
            var instructions = string.Join("\n\n", turns.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            instructions += $"\nYou are a text-only assistant. Answer only the supplied conversation. No tools are available. Keep the answer within {outputLimit} tokens.";
            await File.WriteAllTextAsync(instructionsFile, instructions, callToken);
            var finalFile = Path.Combine(root, "final.txt");
            var arguments = new List<string>
            {
                "exec", "--ignore-user-config", "--ignore-rules", "--strict-config", "--ephemeral",
                "--skip-git-repo-check", "--json", "--color", "never", "-C", root, "-m", model,
                "-s", "read-only", "--output-last-message", finalFile
            };
            foreach (var (key, value) in new Dictionary<string, object>
            {
                ["model_catalog_json"] = catalogFile, ["model_instructions_file"] = instructionsFile,
                ["instructions"] = string.Empty, ["developer_instructions"] = string.Empty,
                ["model_provider"] = "openai", ["forced_login_method"] = "chatgpt",
                ["cli_auth_credentials_store"] = "file", ["approval_policy"] = "never",
                ["web_search"] = "disabled", ["project_doc_max_bytes"] = 0,
                ["tools.update_plan.enabled"] = false, ["tools.experimental_request_user_input.enabled"] = false,
                ["skills.include_instructions"] = false, ["skills.bundled.enabled"] = false,
                ["include_environment_context"] = false, ["include_collaboration_mode_instructions"] = false,
                ["include_permissions_instructions"] = false, ["include_apps_instructions"] = false,
                ["log_dir"] = Path.Combine(root, "logs"), ["suppress_unstable_features_warning"] = true
            })
            {
                arguments.Add("-c");
                arguments.Add($"{key}={JsonSerializer.Serialize(value)}");
            }
            foreach (var feature in DisabledFeatures) arguments.AddRange(["--disable", feature]);
            arguments.AddRange(["--enable", "skip_host_skill_discovery", "-"]);
            var prompt = JsonSerializer.Serialize(turns.Where(m => m.Role != ChatRole.System).Select(m => new
            { role = m.Role.Value, author = m.AuthorName, text = m.Text }));
            var result = await Run(arguments, environment, root, prompt, callToken);
            var response = ParseResult(result, model);
            // --output-last-message contains the final answer, unlike intermediate agent messages.
            var maxChars = checked(outputLimit * 8);
            var file = new FileInfo(finalFile);
            if (!file.Exists || file.Length > maxChars * 4L) throw Failure("final-output");
            var final = await File.ReadAllTextAsync(finalFile, callToken);
            if (final.Length > maxChars || !string.Equals(final.TrimEnd(), response.Text.TrimEnd(), StringComparison.Ordinal))
                throw Failure("final-output");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("codex-cli call timed out."); }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { throw; }
        catch (ModelLimitReachedException) { throw; }
        catch (ProviderFailureException) { throw; }
        catch (Exception) { throw Failure("process"); }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, string> CreateEnvironment(string root)
    {
        var result = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            ["HOME"] = root, ["USERPROFILE"] = root, ["CODEX_HOME"] = settings.HomeDirectory,
            ["TMPDIR"] = root, ["TEMP"] = root, ["TMP"] = root, ["TZ"] = "UTC",
            ["OTEL_SDK_DISABLED"] = "true"
        };
        if (OperatingSystem.IsWindows())
            foreach (var name in new[] { "SYSTEMROOT", "WINDIR" })
                if (Environment.GetEnvironmentVariable(name) is { } value) result[name] = value;
        return result;
    }

    private async Task<ProcessRunResult> Run(IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, string root, string input, CancellationToken token)
    {
        var result = await runner.RunAsync(new ProcessRunRequest(settings.ExecutablePath, arguments,
            environment, root, input, TimeSpan.FromSeconds(settings.CallTimeoutSeconds)), token);
        if (result.TimedOut) throw new TimeoutException("codex-cli call timed out.");
        return result;
    }

    private static JsonObject PrepareCatalog(string json, string model)
    {
        var models = JsonNode.Parse(json)?["models"]?.AsArray() ?? throw Failure("catalog");
        var selected = models.OfType<JsonObject>().SingleOrDefault(m => m["slug"]?.GetValue<string>() == model)
            ?? throw Failure("model-not-in-pinned-catalog");
        selected = (JsonObject)selected.DeepClone();
        selected["shell_type"] = "disabled";
        selected["apply_patch_tool_type"] = null;
        selected["experimental_supported_tools"] = new JsonArray();
        selected["tool_mode"] = "direct";
        selected["model_messages"] = null;
        selected["base_instructions"] = "You are a text-only assistant. No tools are available.";
        selected["include_skills_usage_instructions"] = false;
        selected["include_plugin_usage_instructions"] = false;
        selected["include_apps_usage_instructions"] = false;
        selected["supports_search_tool"] = false;
        selected["supports_experimental_context"] = false;
        return new JsonObject { ["models"] = new JsonArray(selected) };
    }

    private static ChatResponse ParseResult(ProcessRunResult result, string model)
    {
        string? final = null;
        UsageDetails? usage = null;
        var complete = false;
        var failed = false;
        var errors = new List<string>();
        try
        {
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is "error" or "turn.failed")
                {
                    failed = true;
                    errors.Add(root.ToString()); // in memory only; never returned/logged.
                }
                else if (type == "item.completed")
                {
                    var item = root.GetProperty("item");
                    switch (item.GetProperty("type").GetString())
                    {
                        case "agent_message": final = item.GetProperty("text").GetString(); break;
                        case "reasoning": break;
                        case "error": failed = true; errors.Add(item.ToString()); break;
                        default: throw Failure("unexpected-tool-output");
                    }
                }
                else if (type == "turn.completed")
                {
                    complete = true;
                    var counts = root.GetProperty("usage");
                    var input = counts.GetProperty("input_tokens").GetInt64();
                    var output = counts.GetProperty("output_tokens").GetInt64();
                    var cached = counts.TryGetProperty("cached_input_tokens", out var cache) ? cache.GetInt64() : 0;
                    if (input < 0 || output < 0 || cached < 0 || cached > input) throw Failure("usage");
                    usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output,
                        CachedInputTokenCount = cached, TotalTokenCount = checked(input + output) };
                }
                else if (type is not "thread.started" and not "turn.started") throw Failure("unexpected-event");
            }
        }
        catch (JsonException) { throw Failure("invalid-json"); }
        catch (KeyNotFoundException) { throw Failure("invalid-json"); }
        if (failed || result.ExitCode != 0)
        {
            var error = string.Join('\n', errors) + "\n" + result.StandardError;
            if (ContainsAny(error, "unauthorized", "401", "authentication", "not logged in", "please log in", "refresh token", "invalid_api_key"))
                throw Failure("authentication");
            if (ContainsAny(error, "usage_limit_reached", "usage limit", "rate_limit", "rate limit", "quota", "429"))
                throw new ModelLimitReachedException("codex-cli subscription limit reached.", LlmLimitScope.Provider);
            throw Failure("process");
        }
        if (!complete || string.IsNullOrWhiteSpace(final) || usage is null) throw Failure("incomplete-result");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, final))
        { ModelId = model, Usage = usage, AdditionalProperties = new AdditionalPropertiesDictionary
            { [LlmResponseKeys.ReportedCostUsd] = 0m } };
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static ProviderFailureException Failure(string category) => new(category);
    private sealed class ProviderFailureException(string category)
        : InvalidOperationException($"codex-cli unavailable ({category}).");
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
