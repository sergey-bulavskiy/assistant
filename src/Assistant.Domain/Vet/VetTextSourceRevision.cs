namespace Assistant.Domain.Vet;

public sealed class VetTextSourceRevision
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public Guid SourceId { get; set; }
    public int Ordinal { get; set; }
    public string Text { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long UpdateId { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public bool IsEdit { get; set; }
    public Guid OperationKey { get; set; }
    public string State { get; set; } = "admitted";
    public Guid? ExtractionResultId { get; set; }
    public Guid? AttemptId { get; set; }
    public int ExplicitRetryCount { get; set; }
    public string? FailureCategory { get; set; }
    public string? AnswerText { get; set; }
    public string AnswerState { get; set; } = "none";
    public string? WorkJson { get; set; }
    public string? HistoryJson { get; set; }
}
