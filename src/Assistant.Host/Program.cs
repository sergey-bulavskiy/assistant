using Assistant.Application;
using Assistant.Application.Common;
using Assistant.Host;
using Assistant.Infrastructure;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

// Force BotOptions validation now, before touching the database: a bad TELEGRAM_BOT_TOKEN or
// ALLOWED_USER_IDS should fail fast with an OptionsValidationException rather than running
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

await app.RunAsync();
return 0;

public partial class Program;
