using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Llm;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Common;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.ClaudeCli;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
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

        services.AddScoped<IMessageStore, MessageStore>();
        services.AddScoped<ICurrentFamily, CurrentFamily>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IManagerUpdateHandler, ManagerUpdateHandler>();
        services.AddSingleton<IClaimCodeProvider, ClaimCodeProvider>();
        services.AddScoped<IPendingBotCreations, PendingBotCreations>();
        services.AddScoped<IChatSettingsStore, ChatSettingsStore>();

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
            ModelCooldownMinutesRaw = configuration["LLM_MODEL_COOLDOWN_MINUTES"] ?? string.Empty
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

        var llmParseResult = LlmConfigParser.Parse(llmOptions, LlmProviderValidation.Create(claudeCliOptionsForValidation));
        var llmEnabled = llmParseResult.IsEnabled;
        var llmConfig = llmParseResult.Config;

        services.AddSingleton(new LlmStartupResult(llmEnabled, llmParseResult.Errors));
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
            services.AddSingleton<IChatClient>(sp => new ClaudeCliChatClient(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<ClaudeCliOptions>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatClient>>()));
            services.AddSingleton<IChatClientProvider>(sp => new ChatClientProvider(new Dictionary<string, IChatClient>
            {
                [LlmProviderValidation.ClaudeCliPrefix] = sp.GetRequiredService<IChatClient>()
            }));
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
            services.AddHostedService(sp => new ClaudeCliInstallerHostedService(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<ClaudeCliOptions>(),
                sp.GetRequiredService<IModelAvailability>(),
                claudeCliModelNames,
                sp.GetRequiredService<ILogger<ClaudeCliInstallerHostedService>>()));
            services.AddSingleton(new ConcurrentCallGate(llmConfig.MaxConcurrentCalls));
            services.AddScoped<IBudgetGuard, BudgetGuard>();
            services.AddScoped<IBudgetNoticeSender, BudgetNoticeSender>();
            services.AddScoped<ILlmGateway, LlmGateway>();
        }
        else
        {
            services.AddSingleton<ILlmGateway, NullLlmGateway>();
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
}
