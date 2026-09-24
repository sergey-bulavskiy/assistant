namespace Assistant.Domain.Messages;

public class ChatMigration
{
    public long FromChatId { get; set; }
    public long ToChatId { get; set; }
    public DateTimeOffset MigratedAt { get; set; }
}
