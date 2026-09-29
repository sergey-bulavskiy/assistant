namespace Assistant.Infrastructure.Bots;

public record PollingWorkerSettings(
    TimeSpan MinBackoff,
    TimeSpan MaxBackoff,
    int LongPollTimeoutSeconds,
    int PoisonUpdateFailureCap = 5)
{
    public static PollingWorkerSettings Default { get; } = new(
        MinBackoff: TimeSpan.FromSeconds(1),
        MaxBackoff: TimeSpan.FromSeconds(60),
        LongPollTimeoutSeconds: 30,
        PoisonUpdateFailureCap: 5);
}
