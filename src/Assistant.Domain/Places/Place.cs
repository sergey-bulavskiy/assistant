namespace Assistant.Domain.Places;

public class Place
{
    public long Id { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public PlaceStatus Status { get; set; }

    /// <summary>General assistant only: answer every ordinary text message in this place, not just
    /// mentions and replies. Each row has its own flag; a topic never inherits its chat-wide row's.</summary>
    public bool ReplyToAll { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
