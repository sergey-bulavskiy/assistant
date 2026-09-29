namespace Assistant.Domain.Families;

public class FamilyMember
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long TelegramUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public FamilyMemberStatus Status { get; set; }
    public bool IsOwner { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
