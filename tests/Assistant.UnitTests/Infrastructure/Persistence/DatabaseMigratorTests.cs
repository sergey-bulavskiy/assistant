using Assistant.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Assistant.UnitTests.Infrastructure.Persistence;

public class DatabaseMigratorTests
{
    [Fact]
    public async Task Retries_on_npgsql_exception_then_succeeds()
    {
        var attempts = 0;

        Task Migrate(CancellationToken ct)
        {
            attempts++;
            if (attempts < 3)
            {
                throw new NpgsqlException("simulated: database not ready");
            }

            return Task.CompletedTask;
        }

        await DatabaseMigrator.MigrateAsync(Migrate, NullLogger.Instance, CancellationToken.None, maxAttempts: 5, delay: TimeSpan.Zero);

        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Throws_after_max_attempts_exhausted()
    {
        var attempts = 0;

        Task Migrate(CancellationToken ct)
        {
            attempts++;
            throw new NpgsqlException("simulated: database never ready");
        }

        await Should.ThrowAsync<NpgsqlException>(() =>
            DatabaseMigrator.MigrateAsync(Migrate, NullLogger.Instance, CancellationToken.None, maxAttempts: 3, delay: TimeSpan.Zero));

        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Non_npgsql_exceptions_are_not_retried()
    {
        var attempts = 0;

        Task Migrate(CancellationToken ct)
        {
            attempts++;
            throw new InvalidOperationException("not a database error");
        }

        await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseMigrator.MigrateAsync(Migrate, NullLogger.Instance, CancellationToken.None, maxAttempts: 5, delay: TimeSpan.Zero));

        attempts.ShouldBe(1);
    }
}
