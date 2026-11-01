using System.Net;
using System.Text.Json;
using Npgsql;

namespace Assistant;

public static class TelegramPolling
{
    public static async Task RunAsync(Func<long, CancellationToken, Task<JsonElement>> getUpdates,
        Func<JsonElement, CancellationToken, Task> handle, TimeSpan retryDelay, CancellationToken stop)
    {
        long offset = 0;
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var updates = await getUpdates(offset, stop);
                foreach (var update in updates.EnumerateArray())
                {
                    await handle(update, stop);
                    offset = checked(update.GetProperty("update_id").GetInt64() + 1);
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) when (IsTransient(error))
            {
                if (++failures >= 5) throw;
                try { await Task.Delay(retryDelay, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }
    }

    private static bool IsTransient(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException) return true;
            if (current is NpgsqlException { IsTransient: true }) return true;
            if (current is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500 && (int)http.StatusCode <= 599))
                return true;
        }
        return false;
    }
}
