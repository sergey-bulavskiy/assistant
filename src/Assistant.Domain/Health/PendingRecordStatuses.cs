namespace Assistant.Domain.Health;

/// <summary>Values of pending_records.status. Only a pending row can change, exactly once.</summary>
public static class PendingRecordStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Expired = "expired";
}
