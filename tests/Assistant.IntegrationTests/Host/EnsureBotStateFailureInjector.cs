namespace Assistant.IntegrationTests.Host;

/// <summary>
/// Shared (singleton) counter that <see cref="FlakyMessageStore"/> consults. PollingService opens a
/// fresh DI scope — and therefore a fresh scoped <c>IMessageStore</c> instance — for every retry of
/// EnsureBotState, so the "fail the first N attempts" state has to live outside any one scoped
/// instance for the failures to actually span retries.
/// </summary>
public sealed class EnsureBotStateFailureInjector
{
    private int _remaining;

    public void FailNextTimes(int times) => Interlocked.Exchange(ref _remaining, times);

    public bool ShouldFail()
    {
        while (true)
        {
            var current = Volatile.Read(ref _remaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}
