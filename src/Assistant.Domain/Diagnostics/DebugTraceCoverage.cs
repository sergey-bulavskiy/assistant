namespace Assistant.Domain.Diagnostics;

public sealed class DebugTraceCoverage
{
    public int Id { get; set; } = 1;
    public long RetainedBytes { get; set; }
    public long EvictedCount { get; set; }
    public DateTimeOffset? LastEvictedAt { get; set; }
}
