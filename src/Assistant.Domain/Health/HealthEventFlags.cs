namespace Assistant.Domain.Health;

/// <summary>Values of events.flags, set by the safety rules when an event is saved.</summary>
public static class HealthEventFlags
{
    /// <summary>A glucose reading at or above the target of its context (no message is sent).</summary>
    public const string OutOfTarget = "out_of_target";

    /// <summary>An alert level was reached, but the reading was older than the alert age limit:
    /// recorded without an alert.</summary>
    public const string OldValueNotAlerted = "old_value_not_alerted";
}
