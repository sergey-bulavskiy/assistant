namespace Assistant.Domain.Health;

/// <summary>Values of events.occurred_at_source.</summary>
public static class OccurredAtSources
{
    /// <summary>The day or time was stated in the message.</summary>
    public const string Stated = "stated";

    /// <summary>No time in the message: the message's own send time.</summary>
    public const string Message = "message";
}
