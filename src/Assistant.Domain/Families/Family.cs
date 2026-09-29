namespace Assistant.Domain.Families;

public class Family
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
