using Microsoft.Extensions.Logging;
using Npgsql;

namespace Assistant.Infrastructure.Persistence;

public static class DatabaseMigrator
{
    public static async Task MigrateAsync(
        Func<CancellationToken, Task> migrate,
        ILogger logger,
        CancellationToken cancellationToken,
        int maxAttempts = 30,
        TimeSpan? delay = null)
    {
        var actualDelay = delay ?? TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await migrate(cancellationToken);
                logger.LogInformation("database migrations applied");
                return;
            }
            catch (NpgsqlException ex)
            {
                if (attempt == maxAttempts)
                {
                    logger.LogError(
                        ex,
                        "database still unreachable after {MaxAttempts} attempts, giving up: {ExceptionType}",
                        maxAttempts, ex.GetType().Name);
                    throw;
                }

                logger.LogWarning(
                    "database not ready (attempt {Attempt}/{MaxAttempts}): {ExceptionType}",
                    attempt, maxAttempts, ex.GetType().Name);
                await Task.Delay(actualDelay, cancellationToken);
            }
        }
    }
}
