namespace Assistant.Host;

public record PollingSettings(
    TimeSpan MinBackoff,
    TimeSpan MaxBackoff,
    int LongPollTimeoutSeconds,
    int PoisonUpdateFailureCap = 5)
{
    public static PollingSettings Default { get; } = new(
        MinBackoff: TimeSpan.FromSeconds(1),
        MaxBackoff: TimeSpan.FromSeconds(60),
        LongPollTimeoutSeconds: 30,
        PoisonUpdateFailureCap: 5);
}
