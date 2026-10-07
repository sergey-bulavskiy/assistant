using System.Net;
using Assistant.Application.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Assistant.IntegrationTests.Host;

/// <summary>Task 9: the app always starts regardless of LLM config, and ILlmGateway resolves to
/// the real pipeline only when config is present and fully valid (spec 3.1, 8.9). Never invokes a
/// real `claude` binary -- IProcessRunner/ClaudeCliInstallerHostedService are out of scope here
/// (Task 10, not implemented).
///
/// AddInfrastructure reads LLM_*/CLAUDE_* config eagerly (at service-registration time, before
/// WebHost.Build() runs) -- exactly like production Program.cs, which reads a config that is
/// already final by the time it runs. AssistantWebApplicationFactory's ConfigureAppConfiguration
/// override is applied to the builder later than that (inside WebApplicationFactory's Build()
/// interception), so it is invisible to this eager read; process environment variables, read by
/// the default configuration providers during WebApplication.CreateBuilder(args) itself (before
/// any of Program.cs's later statements run), are not. Hence these tests set process environment
/// variables rather than passing extra config to the factory. Each test restores its variables in
/// a `finally` immediately after the host has finished starting (by which point the eager read has
/// already happened and is baked into the built DI container), restoring each original value. The
/// collection serializes these tests with other host factory tests.</summary>
[Collection(HostFactoryCollection.Name)]
public class LlmWiringTests : IAsyncLifetime
{
    private TestDatabaseLease? _database;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await IntegreSqlPool.CreateTestDatabaseAsync();
        _connectionString = _database.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task WaitUntilHealthyAsync(HttpClient client)
    {
        for (var i = 0; i < 400; i++)
        {
            var response = await client.GetAsync("/health");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("host did not become healthy in time");
    }

    private static readonly string[] LlmEnvVarNames =
    {
        "LLM_MODELS", "LLM_CALLS_PER_MINUTE", "LLM_CALLS_PER_DAY", "LLM_MAX_CONTEXT_MESSAGES",
        "LLM_MAX_INPUT_CHARS", "LLM_MAX_OUTPUT_TOKENS", "LLM_CALL_TIMEOUT_SECONDS",
        "LLM_MAX_CONCURRENT_CALLS", "LLM_MODEL_COOLDOWN_MINUTES", "CLAUDE_CODE_OAUTH_TOKEN",
        // M3b additions (Task 9): API keys/proxies/prices/budget, also restored around each
        // StartWithEnvAsync call so no test leaks one of these into the next.
        "LLM_PRICES", "ANTHROPIC_API_KEY", "ANTHROPIC_PROXY", "OPENAI_API_KEY", "OPENAI_BASE_URL",
        "OPENAI_PROXY", "LLM_BUDGET_DAILY_USD", "LLM_BUDGET_MONTHLY_USD", "LLM_BUDGET_WARN_PERCENT",
        "LLM_BUDGET_HARD_PERCENT", "LLM_FAST_MODELS"
    };

    // Shared valid base for every M3b test below: claude-cli:sonnet always works (no API key
    // needed beyond the OAuth token), so these tests only vary the anthropic-specific variables.
    private static Dictionary<string, string?> ValidBaseEnv(string models) => new()
    {
        ["LLM_MODELS"] = models,
        ["LLM_CALLS_PER_MINUTE"] = "10",
        ["LLM_CALLS_PER_DAY"] = "200",
        ["LLM_MAX_CONTEXT_MESSAGES"] = "30",
        ["LLM_MAX_INPUT_CHARS"] = "40000",
        ["LLM_MAX_OUTPUT_TOKENS"] = "4000",
        ["LLM_CALL_TIMEOUT_SECONDS"] = "120",
        ["LLM_MAX_CONCURRENT_CALLS"] = "2",
        ["LLM_MODEL_COOLDOWN_MINUTES"] = "30",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "test-oauth-token",
        ["LLM_PRICES"] = "claude-haiku-4-5=1/5",
        ["LLM_BUDGET_DAILY_USD"] = "2",
        ["LLM_BUDGET_MONTHLY_USD"] = "30"
    };

    private async Task<AssistantWebApplicationFactory> StartWithEnvAsync(IReadOnlyDictionary<string, string?> env)
    {
        var originalValues = LlmEnvVarNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in LlmEnvVarNames)
            {
                Environment.SetEnvironmentVariable(name, env.TryGetValue(name, out var value) ? value : null);
            }

            var factory = new AssistantWebApplicationFactory(_connectionString);
            try
            {
                var client = factory.CreateClient();
                await WaitUntilHealthyAsync(client);
                return factory;
            }
            catch
            {
                // An undisposed host keeps connections open, which would block releasing the test database.
                factory.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var name in LlmEnvVarNames)
            {
                Environment.SetEnvironmentVariable(name, originalValues[name]);
            }
        }
    }

    [Fact]
    public async Task No_LLM_vars_set_host_starts_and_gateway_is_the_null_gateway()
    {
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>());

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();

        gateway.ShouldBeOfType<NullLlmGateway>();
        gateway.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Valid_LLM_config_host_starts_and_gateway_is_the_real_gateway_with_the_configured_catalog()
    {
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>
        {
            ["LLM_MODELS"] = "claude-cli:sonnet,claude-cli:haiku",
            ["LLM_CALLS_PER_MINUTE"] = "10",
            ["LLM_CALLS_PER_DAY"] = "200",
            ["LLM_MAX_CONTEXT_MESSAGES"] = "30",
            ["LLM_MAX_INPUT_CHARS"] = "40000",
            ["LLM_MAX_OUTPUT_TOKENS"] = "4000",
            ["LLM_CALL_TIMEOUT_SECONDS"] = "120",
            ["LLM_MAX_CONCURRENT_CALLS"] = "2",
            ["LLM_MODEL_COOLDOWN_MINUTES"] = "30",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "test-oauth-token"
        });

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();

        gateway.ShouldBeOfType<LlmGateway>();
        gateway.IsEnabled.ShouldBeTrue();

        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
    }

    [Fact]
    public async Task LLM_MODELS_set_but_a_limit_missing_host_still_starts_and_gateway_is_the_null_gateway()
    {
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>
        {
            ["LLM_MODELS"] = "claude-cli:sonnet",
            // LLM_CALLS_PER_MINUTE (and every other limit) deliberately missing -- an invalid/
            // absent limit must turn LLM off, never crash startup (spec 8.9).
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "test-oauth-token"
        });

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        var startupResult = scope.ServiceProvider.GetRequiredService<LlmStartupResult>();

        gateway.ShouldBeOfType<NullLlmGateway>();
        startupResult.IsEnabled.ShouldBeFalse();
        startupResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task LLM_MODELS_set_but_no_oauth_token_host_still_starts_and_gateway_is_the_null_gateway()
    {
        // N3: a claude-cli entry with no CLAUDE_CODE_OAUTH_TOKEN fails LlmProviderValidation and is
        // dropped (spec 8.9) -- with no other entry left, LlmConfigParser reports zero valid models
        // and the whole config is off, same as if LLM_MODELS had never been set, not a startup crash.
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>
        {
            ["LLM_MODELS"] = "claude-cli:sonnet",
            ["LLM_CALLS_PER_MINUTE"] = "10",
            ["LLM_CALLS_PER_DAY"] = "200",
            ["LLM_MAX_CONTEXT_MESSAGES"] = "30",
            ["LLM_MAX_INPUT_CHARS"] = "40000",
            ["LLM_MAX_OUTPUT_TOKENS"] = "4000",
            ["LLM_CALL_TIMEOUT_SECONDS"] = "120",
            ["LLM_MAX_CONCURRENT_CALLS"] = "2",
            ["LLM_MODEL_COOLDOWN_MINUTES"] = "30"
            // CLAUDE_CODE_OAUTH_TOKEN deliberately missing.
        });

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();

        gateway.ShouldBeOfType<NullLlmGateway>();
        gateway.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_provider_host_still_starts_and_gateway_is_the_null_gateway()
    {
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>
        {
            ["LLM_MODELS"] = "unknown-provider:sonnet",
            ["LLM_CALLS_PER_MINUTE"] = "10",
            ["LLM_CALLS_PER_DAY"] = "200",
            ["LLM_MAX_CONTEXT_MESSAGES"] = "30",
            ["LLM_MAX_INPUT_CHARS"] = "40000",
            ["LLM_MAX_OUTPUT_TOKENS"] = "4000",
            ["LLM_CALL_TIMEOUT_SECONDS"] = "120",
            ["LLM_MAX_CONCURRENT_CALLS"] = "2",
            ["LLM_MODEL_COOLDOWN_MINUTES"] = "30"
        });

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();

        gateway.ShouldBeOfType<NullLlmGateway>();
    }

    [Fact]
    public async Task With_an_anthropic_entry_and_an_ANTHROPIC_API_KEY_set_the_entry_resolves()
    {
        var env = ValidBaseEnv("claude-cli:sonnet,anthropic:claude-haiku-4-5");
        env["ANTHROPIC_API_KEY"] = "test-anthropic-key";

        using var factory = await StartWithEnvAsync(env);

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        gateway.ShouldBeOfType<LlmGateway>();
        gateway.IsEnabled.ShouldBeTrue();

        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet", "claude-haiku-4-5" });

        // Constructing the client never contacts the network -- this only proves DI wired the
        // entry through to a resolvable IChatClient, not that a real call would succeed.
        var chatClients = scope.ServiceProvider.GetRequiredService<IChatClientProvider>();
        Should.NotThrow(() => chatClients.GetClient("anthropic", "claude-haiku-4-5"));
    }

    [Fact]
    public async Task With_an_anthropic_entry_and_no_ANTHROPIC_API_KEY_the_entry_is_dropped_and_claude_cli_still_works()
    {
        var env = ValidBaseEnv("claude-cli:sonnet,anthropic:claude-haiku-4-5");
        // ANTHROPIC_API_KEY deliberately missing.

        using var factory = await StartWithEnvAsync(env);

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        gateway.ShouldBeOfType<LlmGateway>();
        gateway.IsEnabled.ShouldBeTrue();

        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet" });

        var startupResult = scope.ServiceProvider.GetRequiredService<LlmStartupResult>();
        startupResult.Errors.ShouldContain(e => e.Contains("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public async Task An_invalid_ANTHROPIC_PROXY_drops_the_anthropic_entry_and_claude_cli_still_works()
    {
        var env = ValidBaseEnv("claude-cli:sonnet,anthropic:claude-haiku-4-5");
        env["ANTHROPIC_API_KEY"] = "test-anthropic-key";
        env["ANTHROPIC_PROXY"] = "not-a-valid-proxy-url";

        using var factory = await StartWithEnvAsync(env);

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        gateway.ShouldBeOfType<LlmGateway>();
        gateway.IsEnabled.ShouldBeTrue();

        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet" });

        var startupResult = scope.ServiceProvider.GetRequiredService<LlmStartupResult>();
        startupResult.Errors.ShouldContain(e => e.Contains("ANTHROPIC_PROXY") && !e.Contains("not-a-valid-proxy-url"));
    }

    [Fact]
    public async Task An_invalid_OPENAI_BASE_URL_drops_the_openai_entry_and_claude_cli_still_works()
    {
        var env = ValidBaseEnv("claude-cli:sonnet,openai:gpt-6-luna");
        env["OPENAI_API_KEY"] = "test-openai-key";
        env["OPENAI_BASE_URL"] = "not-a-valid-url";
        env["LLM_PRICES"] = "claude-haiku-4-5=1/5,gpt-6-luna=1/5";

        using var factory = await StartWithEnvAsync(env);

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        gateway.ShouldBeOfType<LlmGateway>();
        gateway.IsEnabled.ShouldBeTrue();

        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet" });

        var startupResult = scope.ServiceProvider.GetRequiredService<LlmStartupResult>();
        startupResult.Errors.ShouldContain(e => e.Contains("OPENAI_BASE_URL") && !e.Contains("not-a-valid-url"));

        // Constructing every other provider's client must still work -- the whole
        // IChatClientProvider singleton factory must not have thrown.
        var chatClients = scope.ServiceProvider.GetRequiredService<IChatClientProvider>();
        Should.NotThrow(() => chatClients.GetClient("claude-cli", "sonnet"));
    }

    [Fact]
    public async Task UsageCommandHandler_resolves_even_when_LLM_is_entirely_off()
    {
        using var factory = await StartWithEnvAsync(new Dictionary<string, string?>());

        using var scope = factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
        gateway.ShouldBeOfType<NullLlmGateway>();

        var usageHandler = scope.ServiceProvider.GetRequiredService<Assistant.Infrastructure.Manager.UsageCommandHandler>();
        usageHandler.ShouldNotBeNull();
    }
}
