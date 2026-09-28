namespace Assistant.Domain.Places;

public class Place
{
    public long Id { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public PlaceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
