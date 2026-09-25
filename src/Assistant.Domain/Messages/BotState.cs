namespace Assistant.Domain.Messages;

public class BotState
{
    public long BotId { get; set; }
    public string Username { get; set; } = string.Empty;
    public long LastUpdateId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
