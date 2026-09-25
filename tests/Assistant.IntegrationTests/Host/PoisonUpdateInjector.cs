namespace Assistant.IntegrationTests.Host;

/// <summary>
/// Marks a single update id whose message-carrying <c>StoreAsync</c> call must always throw,
/// simulating a permanently poisonous update (e.g. a payload that trips some DB constraint every
/// time it's retried). The offset-only <c>StoreAsync</c> call PollingService's poison-cap logic
/// makes to skip past it (message: null) is not affected — it must keep succeeding, otherwise the
/// update could never be skipped.
/// </summary>
public sealed class PoisonUpdateInjector
{
    private readonly object _lock = new();
    private long? _poisonUpdateId;

    public void AlwaysFailStoringUpdate(long updateId)
    {
        lock (_lock)
        {
            _poisonUpdateId = updateId;
        }
    }

    public bool ShouldFail(long updateId, bool hasMessage)
    {
        lock (_lock)
        {
            return hasMessage && _poisonUpdateId == updateId;
        }
    }
}
