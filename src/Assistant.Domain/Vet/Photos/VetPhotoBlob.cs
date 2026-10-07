namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoBlob
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public string ContentHash { get; set; } = "";
    public long ActualBytes { get; set; }
    public string Format { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[]? Content { get; set; }
    public string State { get; set; } = "retained";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReclaimRequestedAt { get; set; }
    public DateTimeOffset? ReclaimedAt { get; set; }
}
