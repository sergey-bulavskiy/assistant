using Assistant.Application;
using Assistant.Application.Common;
using Assistant.Application.Manager;
using Assistant.Host;
using Assistant.Infrastructure;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

if (args.Contains("--healthcheck"))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(new CompactJsonFormatter());
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAssistantHost(builder.Configuration);

var app = builder.Build();

// Force BotOptions validation now, before touching the database: a missing
// TELEGRAM_MANAGER_BOT_TOKEN or a bad TOKEN_ENCRYPTION_KEY should fail fast with an OptionsValidationException rather than running
// migrations against a database the bot then can't actually use.
_ = app.Services.GetRequiredService<IOptions<BotOptions>>().Value;

app.MapGet("/health", HealthEndpoint.HandleAsync);

// DatabaseMigrator retries transient Postgres connectivity failures, then rethrows once attempts
// are exhausted. An unhandled exception here crashes the process; under the container's restart
// policy that means Docker restarts it and migrations are retried from scratch on the next boot —
// this process does not keep running degraded and serving /health as 503.
var migrationMaxAttempts = app.Configuration.GetValue("Database:MigrationMaxAttempts", 30);
var migrationRetryDelaySeconds = app.Configuration.GetValue("Database:MigrationRetryDelaySeconds", 2.0);

await DatabaseMigrator.MigrateAsync(
    async ct =>
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        await db.Database.MigrateAsync(ct);
    },
    app.Logger,
    CancellationToken.None,
    maxAttempts: migrationMaxAttempts,
    delay: TimeSpan.FromSeconds(migrationRetryDelaySeconds));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
    if (!await db.Families.IgnoreQueryFilters().AnyAsync())
    {
        var claimCode = scope.ServiceProvider.GetRequiredService<IClaimCodeProvider>();
        app.Logger.LogInformation("Platform not claimed yet. Send /claim {ClaimCode} to the manager bot.", claimCode.Code);
    }
}

using (var scope = app.Services.CreateScope())
{
    // One line either way (spec 3.2/8.9): never the token or any other config value, model names
    // only -- an invalid/absent LLM_MODELS must never crash startup (LlmStartupResult/LlmConfig?
    // are always registered, see AddInfrastructure).
    var llmStartup = scope.ServiceProvider.GetRequiredService<LlmStartupResult>();
    foreach (var error in llmStartup.Errors)
    {
        app.Logger.LogError("LLM configuration error: {Error}", error);
    }

    foreach (var warning in llmStartup.Warnings)
    {
        app.Logger.LogWarning("LLM configuration warning: {Warning}", warning);
    }

    if (llmStartup.IsEnabled)
    {
        var llmConfig = scope.ServiceProvider.GetService<Assistant.Application.Common.LlmConfig>();
        var modelNames = llmConfig?.Models.Select(m => m.Name).ToArray() ?? Array.Empty<string>();
        app.Logger.LogInformation("LLM enabled with {Count} model(s): {Models}", modelNames.Length, string.Join(", ", modelNames));
    }
    else
    {
        app.Logger.LogInformation("LLM disabled");
    }
}

await app.RunAsync();
return 0;

public partial class Program;
