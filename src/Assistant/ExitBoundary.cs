namespace Assistant;

public static class ExitBoundary
{
    public static async Task<int> RunAsync(Func<Task> run, CancellationToken stop)
    {
        try
        {
            await run();
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Application failure: {error.GetType().Name}");
            return 1;
        }
    }
}
