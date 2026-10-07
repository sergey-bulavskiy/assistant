namespace Assistant.Domain.Vet;

public sealed class VetProfile
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public int Revision { get; set; } = 1;
    public string? Name { get; set; }
    public string? TimeZone { get; set; }
    public string? GlucoseUnit { get; set; }
    public string? InsulinUnit { get; set; }
    public string? InsulinProduct { get; set; }
    public string? OwnerContextNote { get; set; }
    public string? ReportedVetGuidance { get; set; }
    public string FieldProvenanceJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}
