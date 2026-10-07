namespace Assistant.Domain.Vet;

public sealed class VetDiaryActionChange
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long ActionId { get; set; }
    public long EventId { get; set; }
    public string? BeforeJson { get; set; }
    public string AfterJson { get; set; } = "";
    public int? BeforeRevision { get; set; }
    public int AfterRevision { get; set; }
}
