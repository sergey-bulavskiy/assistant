using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

internal sealed class OrderingRaceGate(string scopeKey, bool hold, bool requireDocumentSource = false) : DbCommandInterceptor
{
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;
    private bool _sourceHeld;
    public void Release() => _release.TrySetResult();
    private bool Matches(DbCommand command) => command.CommandText.Contains("pg_advisory_xact_lock(61009", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, scopeKey));

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Matches(command))
        {
            if (command.Transaction == null) throw new InvalidOperationException("Ordering lock executed outside transaction.");
            if (requireDocumentSource && !_sourceHeld) throw new InvalidOperationException("Ordering lock acquired before document source row.");
            Arrived.TrySetResult();
        }
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("messages", StringComparison.OrdinalIgnoreCase)) _sourceHeld = true;
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        if (Matches(command) && Interlocked.CompareExchange(ref _held, 1, 0) == 0)
        {
            Acquired.TrySetResult();
            if (hold) await _release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }

    public static async Task ProveBlockedAsync(string connectionString, int winner, int waiter, Task competingOperation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(timeout.Token);
        while (true)
        {
            if (competingOperation.IsCompleted) throw new InvalidOperationException("Competing operation bypassed the ordering lock.");
            await using var command = new NpgsqlCommand("""
                SELECT @winner = ANY(pg_blocking_pids(@waiter)) AND EXISTS (
                    SELECT 1 FROM pg_locks w JOIN pg_locks h
                    ON h.locktype = w.locktype AND h.database IS NOT DISTINCT FROM w.database
                    AND h.classid = w.classid AND h.objid = w.objid AND h.objsubid = w.objsubid
                    WHERE w.pid = @waiter AND NOT w.granted AND h.pid = @winner AND h.granted
                    AND w.locktype = 'advisory' AND w.classid = 61009)
                """, connection);
            command.Parameters.AddWithValue("winner", winner); command.Parameters.AddWithValue("waiter", waiter);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Yield(); timeout.Token.ThrowIfCancellationRequested();
        }
    }

    public static Task JoinAsync(params Task?[] tasks) => Task.WhenAll(tasks.Where(x => x != null).Cast<Task>())
        .WaitAsync(TimeSpan.FromSeconds(20));
}
