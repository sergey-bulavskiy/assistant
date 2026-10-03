using System.Collections;
using Assistant.Application.Common;
using Assistant.Infrastructure;
using Assistant.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Evals;

/// <summary>One real model for live evals, built by the app's own wiring (AddInfrastructure) from the
/// same environment variables the app reads, with LLM_MODELS replaced by EVALS_LLM_MODELS (first entry
/// used). Sends the same messages LlmGateway sends, without the gateway: no database, so no
/// llm_calls rows, per-family limits or budget checks. Never installs the Claude CLI.
/// Only the opt-in live test (EVALS_LIVE=1) creates one.</summary>
public sealed class LiveModel : IDisposable
{
    public const string ModelsVariable = "EVALS_LLM_MODELS";

    // The app gets these from deploy/docker-compose.yml; each LLM_* limit must be set or the LLM is off.
    // Real environment variables win.
    private static readonly Dictionary<string, string?> Defaults = new()
    {
        ["LLM_CALLS_PER_MINUTE"] = "1000",
        ["LLM_CALLS_PER_DAY"] = "1000",
        ["LLM_MAX_CONTEXT_MESSAGES"] = "30",
        ["LLM_MAX_INPUT_CHARS"] = "40000",
        ["LLM_MAX_OUTPUT_TOKENS"] = "1000",
        ["LLM_CALL_TIMEOUT_SECONDS"] = "120",
        ["LLM_MAX_CONCURRENT_CALLS"] = "1",
        ["LLM_MODEL_COOLDOWN_MINUTES"] = "30",
        // Required by AddInfrastructure's DbContext registration; never opened.
        ["ConnectionStrings:Assistant"] = "Host=localhost;Database=unused"
    };

    private readonly ServiceProvider _services;
    private readonly IChatClient _client;
    private readonly ModelCatalogEntry _entry;
    private readonly LlmConfig _config;

    private LiveModel(ServiceProvider services, IChatClient client, ModelCatalogEntry entry, LlmConfig config)
    {
        _services = services;
        _client = client;
        _entry = entry;
        _config = config;
    }

    public string Name => $"{_entry.ProviderPrefix}:{_entry.Name}";

    public static LiveModel FromEnvironment()
    {
        var models = Environment.GetEnvironmentVariable(ModelsVariable);
        if (string.IsNullOrWhiteSpace(models))
        {
            throw new InvalidOperationException($"Set {ModelsVariable} to a provider:model entry (see tests/Assistant.Evals/README.md).");
        }

        var settings = new Dictionary<string, string?>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            settings[(string)variable.Key] = variable.Value as string;
        }

        settings["LLM_MODELS"] = models;
        settings["LLM_FAST_MODELS"] = string.Empty;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        var startup = provider.GetRequiredService<LlmStartupResult>();
        if (!startup.IsEnabled)
        {
            provider.Dispose();
            throw new InvalidOperationException($"No usable model in {ModelsVariable}: {string.Join("; ", startup.Errors)}");
        }

        var config = provider.GetRequiredService<LlmConfig>();
        var entry = config.Models[0];
        var client = provider.GetRequiredService<IChatClientProvider>().GetClient(entry.ProviderPrefix, entry.Name);
        return new LiveModel(provider, client, entry, config);
    }

    /// <summary>Same request shape as LlmGateway: the system prompt, then the message text as the one
    /// user turn (providers add their own framing).</summary>
    public async Task<string> CompleteAsync(string systemPrompt, string text, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt), new(ChatRole.User, text) };
        var options = new ChatOptions { ModelId = _entry.Name, MaxOutputTokens = _config.MaxOutputTokens };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.CallTimeoutSeconds));
        var response = await _client.GetResponseAsync(messages, options, timeout.Token);
        return response.Text;
    }

    public void Dispose() => _services.Dispose();
}
