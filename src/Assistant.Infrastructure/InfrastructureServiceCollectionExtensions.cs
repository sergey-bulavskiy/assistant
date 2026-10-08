using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Infrastructure.Health.Documents;
using Assistant.Application.Llm;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Common;
using Assistant.Infrastructure.Diagnostics;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.ClaudeCli;
using Assistant.Infrastructure.Llm.CodexCli;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Roles;
using Assistant.Infrastructure.Telegram;
using Assistant.Infrastructure.Vet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Assistant.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AssistantDbContext>((_, options) =>
        {
            var connectionString = configuration.GetConnectionString("Assistant")
                ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Assistant' is not configured.");
            AssistantDbContext.Configure(options, connectionString);
        });

        // Private diagnostics use a separate context and transaction for every bounded write.
        // Cleanup stays active when capture is off, so disabling it never extends retention.
        services.AddSingleton(TraceOptions.Parse(configuration));
        services.AddSingleton(new TraceSecretRegistry(configuration));
        services.AddSingleton<TraceCaptureState>();
        services.AddSingleton<TraceRedactor>();
        services.AddSingleton<DebugTraceWriter>();
        services.AddScoped<ITraceSession, TraceSession>();
        services.AddHostedService<TraceCleanupService>();

        services.AddScoped<IMessageStore, MessageStore>();
        services.AddScoped<Assistant.Application.Memory.IGeneralMemoryStore, Assistant.Infrastructure.Memory.GeneralMemoryStore>();
        services.AddScoped<ICurrentFamily, CurrentFamily>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IFamilyOwnership, FamilyOwnership>();
        services.AddScoped<IManagerUpdateHandler, ManagerUpdateHandler>();
        services.AddScoped<UsageCommandHandler>();
        services.AddScoped<SettingsCommandHandler>();
        services.AddSingleton<IClaimCodeProvider, ClaimCodeProvider>();
        services.AddScoped<IPendingBotCreations, PendingBotCreations>();
        services.AddScoped<IChatSettingsStore, ChatSettingsStore>();
        // Always registered (LLM on or off): /tokens only reads llm_calls.
        services.AddScoped<ILlmUsageQuery, LlmUsageQuery>();
        // Health data: scoped on the request's DI scope only (fails closed without ICurrentFamily).
        services.AddScoped<IHealthProfileStore, HealthProfileStore>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<ISafetyAlertStore, SafetyAlertStore>();
        services.AddScoped<IPendingRecordStore, PendingRecordStore>();
        services.AddScoped<IHealthDocumentStore, HealthDocumentStore>();
        services.AddSingleton<IHealthDocumentLeaseKeeper, HealthDocumentLeaseKeeper>();
        services.AddSingleton(TimeProvider.System);
        services.AddVetPersistence();
        services.AddSingleton<IRolePrompts>(_ => new RolePrompts(typeof(RolePrompts).Assembly));
        services.AddSingleton<Assistant.Application.Health.Documents.IDocumentTextExtractor,
            Assistant.Infrastructure.Health.Documents.DocumentTextExtractor>();

        // LLM pipeline (spec 3.1, 8.9): parse config once at composition time and decide on/off.
        // The app must always start -- parsing/validation never throws and nothing here is
        // ValidateOnStart; an invalid or absent config just means NullLlmGateway.
        var llmOptions = new LlmOptions
        {
            ModelsRaw = configuration["LLM_MODELS"] ?? string.Empty,
            CallsPerMinuteRaw = configuration["LLM_CALLS_PER_MINUTE"] ?? string.Empty,
            CallsPerDayRaw = configuration["LLM_CALLS_PER_DAY"] ?? string.Empty,
            MaxContextMessagesRaw = configuration["LLM_MAX_CONTEXT_MESSAGES"] ?? string.Empty,
            MaxInputCharsRaw = configuration["LLM_MAX_INPUT_CHARS"] ?? string.Empty,
            MaxOutputTokensRaw = configuration["LLM_MAX_OUTPUT_TOKENS"] ?? string.Empty,
            CallTimeoutSecondsRaw = configuration["LLM_CALL_TIMEOUT_SECONDS"] ?? string.Empty,
            MaxConcurrentCallsRaw = configuration["LLM_MAX_CONCURRENT_CALLS"] ?? string.Empty,
            ModelCooldownMinutesRaw = configuration["LLM_MODEL_COOLDOWN_MINUTES"] ?? string.Empty,
            // M3b additions (Task 9): LlmConfigParser.ParseModels checks AnthropicApiKey/OpenAiApiKey
            // itself (spec §10.2) -- these must reach LlmOptions here or every anthropic:/openai:
            // entry is dropped as "missing key" regardless of what ANTHROPIC_API_KEY/OPENAI_API_KEY
            // actually hold.
            PricesRaw = configuration["LLM_PRICES"] ?? string.Empty,
            AnthropicApiKey = configuration["ANTHROPIC_API_KEY"] ?? string.Empty,
            AnthropicProxy = configuration["ANTHROPIC_PROXY"] ?? string.Empty,
            OpenAiApiKey = configuration["OPENAI_API_KEY"] ?? string.Empty,
            OpenAiBaseUrl = configuration["OPENAI_BASE_URL"] ?? string.Empty,
            OpenAiProxy = configuration["OPENAI_PROXY"] ?? string.Empty,
            BudgetDailyUsdRaw = configuration["LLM_BUDGET_DAILY_USD"] ?? string.Empty,
            BudgetMonthlyUsdRaw = configuration["LLM_BUDGET_MONTHLY_USD"] ?? string.Empty,
            BudgetWarnPercentRaw = configuration["LLM_BUDGET_WARN_PERCENT"] ?? string.Empty,
            BudgetHardPercentRaw = configuration["LLM_BUDGET_HARD_PERCENT"] ?? string.Empty,
            FastModelsRaw = configuration["LLM_FAST_MODELS"] ?? string.Empty
        };

        // CLAUDE_HOME (spec §8.10): a dedicated, writable directory -- a named Docker volume in
        // production (Task 10, not implemented in this task), never the app's own process HOME.
        // CLAUDE_CLI_VERSION defaults to ClaudeCliOptions.DefaultPinnedVersion so an .env without
        // it still works.
        var claudeHome = configuration["CLAUDE_HOME"] ?? "/home/app/.claude-home";
        var claudeCliVersionRaw = configuration["CLAUDE_CLI_VERSION"];
        var claudeCliVersion = string.IsNullOrWhiteSpace(claudeCliVersionRaw)
            ? ClaudeCliOptions.DefaultPinnedVersion
            : claudeCliVersionRaw;
        var claudeCliOAuthToken = configuration["CLAUDE_CODE_OAUTH_TOKEN"] ?? string.Empty;

        // Built once here (OAuthToken only, no limits yet) purely so LlmConfigParser can validate
        // LLM_MODELS' claude-cli entries against it. MaxOutputTokens/CallTimeoutSeconds are filled
        // in below, from the parsed limits, before the real instance is registered for DI.
        var claudeCliOptionsForValidation = new ClaudeCliOptions
        {
            ExecutablePath = Path.Combine(claudeHome, ".local", "bin", "claude"),
            HomeDirectory = claudeHome,
            PinnedVersion = claudeCliVersion,
            OAuthToken = claudeCliOAuthToken
        };

        var llmParseResult = LlmConfigParser.Parse(llmOptions, LlmProviderValidation.Create(claudeCliOptionsForValidation,
            LlmProviderValidation.RequiresSubscriptionOnly(llmOptions.ModelsRaw, llmOptions.FastModelsRaw)));
        var llmEnabled = llmParseResult.IsEnabled;
        var llmConfig = llmParseResult.Config;
        var llmStartupErrors = new List<string>(llmParseResult.Errors);

        // Proxy validity is provider-wide and can only be checked here -- Application/
        // LlmConfigParser never sees ANTHROPIC_PROXY/OPENAI_PROXY (spec §3.1). An invalid proxy
        // drops every entry of that one provider (one Error each, naming only the variable --
        // ProxyHandlerFactory never returns the URL itself); claude-cli entries and the other
        // provider are unaffected. Done before anything below is registered so every DI consumer
        // of LlmConfig/LlmStartupResult (including the branch just below) sees the final catalog.
        if (llmEnabled && llmConfig is not null)
        {
            var anthropicProxy = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", configuration["ANTHROPIC_PROXY"] ?? string.Empty);
            var openAiProxy = ProxyHandlerFactory.Create("OPENAI_PROXY", configuration["OPENAI_PROXY"] ?? string.Empty);

            // Same reasoning as the proxy validation above: `OpenAiChatClientFactory.Create` does
            // `new Uri(baseUrl)` with no try/catch, inside the IChatClientProvider singleton factory
            // lambda -- an invalid OPENAI_BASE_URL would throw there and break resolution of every
            // entry (including claude-cli), not just openai: ones. Validated here, once, so a bad
            // value just drops the openai: entries (one Error, naming only the variable) the same
            // way an invalid OPENAI_PROXY already does.
            var openAiBaseUrlError = ValidateOpenAiBaseUrl(configuration["OPENAI_BASE_URL"] ?? string.Empty);

            var survivingModels = new List<ModelCatalogEntry>();
            foreach (var entry in llmConfig.Models)
            {
                if (string.Equals(entry.ProviderPrefix, LlmProviderValidation.AnthropicPrefix, StringComparison.OrdinalIgnoreCase) && anthropicProxy.IsFailed)
                {
                    llmStartupErrors.Add($"LLM_MODELS entry 'anthropic:{entry.Name}' dropped: {anthropicProxy.Error}");
                    continue;
                }

                if (string.Equals(entry.ProviderPrefix, LlmProviderValidation.OpenAiPrefix, StringComparison.OrdinalIgnoreCase) && openAiBaseUrlError is not null)
                {
                    llmStartupErrors.Add($"LLM_MODELS entry 'openai:{entry.Name}' dropped: {openAiBaseUrlError}");
                    continue;
                }

                if (string.Equals(entry.ProviderPrefix, LlmProviderValidation.OpenAiPrefix, StringComparison.OrdinalIgnoreCase) && openAiProxy.IsFailed)
                {
                    llmStartupErrors.Add($"LLM_MODELS entry 'openai:{entry.Name}' dropped: {openAiProxy.Error}");
                    continue;
                }

                survivingModels.Add(entry);
            }

            if (survivingModels.Count != llmConfig.Models.Count)
            {
                var survivingFastModels = llmConfig.FastModels.Where(f => survivingModels.Contains(f)).ToArray();
                llmConfig = new LlmConfig
                {
                    Models = survivingModels,
                    CallsPerMinute = llmConfig.CallsPerMinute,
                    CallsPerDay = llmConfig.CallsPerDay,
                    MaxContextMessages = llmConfig.MaxContextMessages,
                    MaxInputChars = llmConfig.MaxInputChars,
                    MaxOutputTokens = llmConfig.MaxOutputTokens,
                    CallTimeoutSeconds = llmConfig.CallTimeoutSeconds,
                    MaxConcurrentCalls = llmConfig.MaxConcurrentCalls,
                    ModelCooldownMinutes = llmConfig.ModelCooldownMinutes,
                    Prices = llmConfig.Prices,
                    Budget = llmConfig.Budget,
                    FastModels = survivingFastModels
                };
            }

            if (survivingModels.Count == 0)
            {
                // Every surviving entry was a paid one with an invalid proxy -- nothing usable is
                // left (spec 8.9: no valid models means LLM is off, same as LLM_MODELS unset).
                llmEnabled = false;
                llmConfig = null;
            }
        }

        services.AddSingleton(new LlmStartupResult(llmEnabled, llmStartupErrors, llmParseResult.Warnings));
        // Always registered, even when null -- GeneralAssistant/consumers take LlmConfig? and treat
        // null as "off" (Decision #6). The factory overload is required here: AddSingleton<T>(instance)
        // throws ArgumentNullException for a null instance, but AddSingleton<T>(factory) does not
        // null-check what the factory returns.
        services.AddSingleton<LlmConfig>(_ => llmConfig!);

        if (llmEnabled && llmConfig is not null)
        {
            var claudeCliOptions = new ClaudeCliOptions
            {
                ExecutablePath = claudeCliOptionsForValidation.ExecutablePath,
                HomeDirectory = claudeHome,
                PinnedVersion = claudeCliVersion,
                OAuthToken = claudeCliOAuthToken,
                MaxOutputTokens = llmConfig.MaxOutputTokens,
                CallTimeoutSeconds = llmConfig.CallTimeoutSeconds
            };
            services.AddSingleton(claudeCliOptions);
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton(new CodexCliOptions
            {
                ExecutablePath = configuration["CODEX_CLI_PATH"] ?? "/usr/local/bin/codex",
                HomeDirectory = configuration["CODEX_HOME"] ?? "/home/app/.codex",
                ImageInputEnabled = !string.Equals(configuration["CODEX_IMAGE_INPUT_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
                MaxOutputTokens = llmConfig.MaxOutputTokens,
                CallTimeoutSeconds = llmConfig.CallTimeoutSeconds
            });
            services.AddSingleton<CodexCliChatClient>();
            services.AddSingleton<IChatClient>(sp => new ClaudeCliChatClient(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<ClaudeCliOptions>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatClient>>()));
            services.AddSingleton<IChatClientProvider>(sp =>
            {
                var clients = new Dictionary<string, IChatClient>
                {
                    [LlmProviderValidation.ClaudeCliPrefix] = sp.GetRequiredService<IChatClient>(),
                    [LlmProviderValidation.CodexCliPrefix] = sp.GetRequiredService<CodexCliChatClient>()
                };

                // One shared HttpClient per provider (not per entry): the M3b execution notes
                // confirmed empirically that neither SDK's IChatClient.Dispose() disposes a
                // passed-in HttpClient, so sharing it across every model entry of that provider is
                // safe. HttpClient.Timeout is infinite -- both factories already set the SDK's own
                // timeout to callTimeout + 5s, and LlmGateway's own CancelAfter(callTimeout) always
                // wins that race; a shorter HttpClient.Timeout would let the HttpClient itself time
                // out first with a bare, unhandled exception instead.
                var anthropicEntries = llmConfig.Models
                    .Where(m => string.Equals(m.ProviderPrefix, LlmProviderValidation.AnthropicPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (anthropicEntries.Length > 0)
                {
                    var proxyResult = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", configuration["ANTHROPIC_PROXY"] ?? string.Empty);
                    var anthropicHttpClient = proxyResult.Handler is null ? new HttpClient() : new HttpClient(proxyResult.Handler);
                    anthropicHttpClient.Timeout = Timeout.InfiniteTimeSpan;
                    var apiKey = configuration["ANTHROPIC_API_KEY"] ?? string.Empty;
                    foreach (var entry in anthropicEntries)
                    {
                        clients[$"{LlmProviderValidation.AnthropicPrefix}:{entry.Name}"] = AnthropicChatClientFactory.Create(
                            apiKey, anthropicHttpClient, entry.Name, TimeSpan.FromSeconds(llmConfig.CallTimeoutSeconds));
                    }
                }

                var openAiEntries = llmConfig.Models
                    .Where(m => string.Equals(m.ProviderPrefix, LlmProviderValidation.OpenAiPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (openAiEntries.Length > 0)
                {
                    var proxyResult = ProxyHandlerFactory.Create("OPENAI_PROXY", configuration["OPENAI_PROXY"] ?? string.Empty);
                    var openAiHttpClient = proxyResult.Handler is null ? new HttpClient() : new HttpClient(proxyResult.Handler);
                    openAiHttpClient.Timeout = Timeout.InfiniteTimeSpan;
                    var apiKey = configuration["OPENAI_API_KEY"] ?? string.Empty;
                    var baseUrl = configuration["OPENAI_BASE_URL"];
                    foreach (var entry in openAiEntries)
                    {
                        clients[$"{LlmProviderValidation.OpenAiPrefix}:{entry.Name}"] = OpenAiChatClientFactory.Create(
                            apiKey, openAiHttpClient, entry.Name, baseUrl, TimeSpan.FromSeconds(llmConfig.CallTimeoutSeconds));
                    }
                }

                return new ChatClientProvider(clients);
            });
            services.AddSingleton(new ModelCatalog(llmConfig));

            // Nit N1: model names are only ever needed by the closures below (this factory and the
            // hosted-service one) -- never registered as a bare IReadOnlyList<string> in DI, which
            // would be an untyped, easy-to-collide-with singleton for anything else that also needs
            // to register a list of strings.
            var claudeCliModelNames = llmConfig.Models.Where(m => m.ProviderPrefix == LlmProviderValidation.ClaudeCliPrefix).Select(m => m.Name).ToArray();

            // Every claude-cli catalog entry starts unavailable at composition time -- before
            // ClaudeCliInstallerHostedService's StartAsync has had a chance to run -- so a request
            // arriving during that window gets AllModelsUnavailable instead of a raw process-launch
            // failure. The hosted service marks each entry available once the pinned CLI version is
            // confirmed installed (or installed successfully). Only one IModelAvailability
            // registration may exist in this branch (Task 9's plain
            // AddSingleton<IModelAvailability, ModelAvailability>() is replaced by this factory).
            services.AddSingleton<IModelAvailability>(sp =>
            {
                var availability = new ModelAvailability(sp.GetRequiredService<IClock>());
                foreach (var modelName in claudeCliModelNames)
                {
                    availability.MarkUnavailable(modelName, DateTimeOffset.MaxValue);
                }

                return availability;
            });
            if (claudeCliModelNames.Length > 0)
            {
                services.AddHostedService(sp => new ClaudeCliInstallerHostedService(
                    sp.GetRequiredService<IProcessRunner>(),
                    sp.GetRequiredService<ClaudeCliOptions>(),
                    sp.GetRequiredService<IModelAvailability>(),
                    claudeCliModelNames,
                    sp.GetRequiredService<ILogger<ClaudeCliInstallerHostedService>>()));
            }
            services.AddSingleton(new ConcurrentCallGate(llmConfig.MaxConcurrentCalls));
            services.AddScoped<IBudgetGuard, BudgetGuard>();
            services.AddScoped<IBudgetNoticeSender, BudgetNoticeSender>();
            // Singleton: BudgetNoticeDispatcher holds only IServiceScopeFactory/ILogger (both already
            // singletons) -- it creates its own scope per dispatch, so it needs no scoped state of its
            // own. Review fix: the reply path must never await the notice check, and must never use
            // the request's own scoped AssistantDbContext for it -- see LlmGateway.CompleteAsync.
            services.AddSingleton<IBudgetNoticeDispatcher, BudgetNoticeDispatcher>();
            services.AddScoped<ILlmGateway, LlmGateway>();
        }
        else
        {
            services.AddSingleton<ILlmGateway, NullLlmGateway>();
            // UsageCommandHandler (always constructed, through ManagerUpdateHandler) takes a plain
            // IBudgetGuard so it never special-cases "LLM off" itself -- NullBudgetGuard's
            // EvaluateAsync returning null already means "no budget configured", same as a real
            // BudgetGuard with LlmConfig.Budget null. IBudgetGuard is otherwise only registered
            // inside the LLM-on branch above (LlmGateway's own dependency); IBudgetNoticeSender is
            // only ever needed by LlmGateway, which isn't registered in this branch, so it needs no
            // fallback registration here.
            services.AddSingleton<IBudgetGuard, NullBudgetGuard>();
        }

        services.AddSingleton<ITokenEncryptor>(sp =>
            new TokenEncryptor(sp.GetRequiredService<IOptions<BotOptions>>().Value.TokenEncryptionKey));

        // Request URLs for every Telegram Bot API call embed the bot token
        // (https://api.telegram.org/bot<token>/method). The default HttpClient logging handlers log
        // request URIs at Information level, which would leak the token into application logs —
        // RemoveAllLoggers() strips those handlers from this named client only.
        services.AddHttpClient("telegram").RemoveAllLoggers();

        services.AddSingleton<ITelegramClientFactory, TelegramClientFactory>();

        services.AddSingleton<PollingHealth>();
        services.AddSingleton(PollingWorkerSettings.Default);
        services.AddSingleton<BotPollingCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<BotPollingCoordinator>());

        return services;
    }

    // Never include the raw value in the error: unlike a proxy URL this one carries no credentials,
    // but keeping the same "name only" shape as ProxyHandlerFactory's errors is simpler to reason
    // about than giving this one variable a special case.
    private static string? ValidateOpenAiBaseUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return "OPENAI_BASE_URL must be an absolute http:// or https:// URL";
        }

        return null;
    }
}
