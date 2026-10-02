using Npgsql;

namespace Assistant.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the test infrastructure itself: IntegreSQL can only recreate (and so re-issue) a returned
/// test database once no connection to it is open. If releasing a lease left Npgsql's idle pooled
/// connections alive, the pool would stall again after a few dozen tests.
/// </summary>
public class TestDatabaseLeaseTests
{
    [Fact]
    public async Task Releasing_a_test_database_closes_the_idle_pooled_connections_to_it()
    {
        var lease = await IntegreSqlPool.CreateTestDatabaseAsync();
        int backendPid;
        try
        {
            await using (var connection = new NpgsqlConnection(lease.ConnectionString))
            {
                await connection.OpenAsync();
                backendPid = connection.ProcessID;
            }

            // Precondition: closing the connection only returned it to Npgsql's pool — the server
            // backend is still there, which is exactly what blocks `DROP DATABASE`.
            (await BackendExistsAsync(lease.ConnectionString, backendPid)).ShouldBeTrue();
        }
        finally
        {
            await lease.DisposeAsync();
        }

        // Matched by backend pid rather than database name: once recreated, the same database name
        // may already be checked out by a test running in parallel. A backend exits asynchronously
        // after its socket is closed, so allow a moment — far below Npgsql's 300s idle lifetime,
        // which is how long the connection would otherwise survive.
        (await BackendGoneWithinAsync(lease.ConnectionString, backendPid, TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    private static async Task<bool> BackendGoneWithinAsync(string testConnectionString, int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await BackendExistsAsync(testConnectionString, pid))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    private static async Task<bool> BackendExistsAsync(string testConnectionString, int pid)
    {
        // A separate, unpooled connection to the maintenance database, so this check neither
        // reuses nor leaves behind a connection to the test database.
        var admin = new NpgsqlConnectionStringBuilder(testConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid)", connection);
        command.Parameters.AddWithValue("pid", pid);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
