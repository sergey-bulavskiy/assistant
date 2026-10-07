namespace Assistant.Domain.Vet;

public sealed class VetExtractionResult
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public Guid InputRevisionId { get; set; }
    public string Json { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string PromptVersion { get; set; } = "vet-text-v1";
    public Guid? AttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
