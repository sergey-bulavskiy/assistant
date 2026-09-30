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
/// variables rather than passing extra config to the factory. xUnit does not run test methods of
/// the same class in parallel, so these don't race each other; each test clears its variables in a
/// `finally` immediately after the host has finished starting (by which point the eager read has
/// already happened and is baked into the built DI container), keeping the window in which another,
/// unrelated test's host could observe a stray value as short as possible.</summary>
public class LlmWiringTests : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = await IntegreSqlPool.CreateTestDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
        "LLM_MAX_CONCURRENT_CALLS", "LLM_MODEL_COOLDOWN_MINUTES", "CLAUDE_CODE_OAUTH_TOKEN"
    };

    private async Task<AssistantWebApplicationFactory> StartWithEnvAsync(IReadOnlyDictionary<string, string?> env)
    {
        foreach (var name in LlmEnvVarNames)
        {
            Environment.SetEnvironmentVariable(name, env.TryGetValue(name, out var value) ? value : null);
        }

        try
        {
            var factory = new AssistantWebApplicationFactory(_connectionString);
            var client = factory.CreateClient();
            await WaitUntilHealthyAsync(client);
            return factory;
        }
        finally
        {
            foreach (var name in LlmEnvVarNames)
            {
                Environment.SetEnvironmentVariable(name, null);
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
}
