namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoCandidate
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid SourceId { get; set; }
    public Guid? BatchId { get; set; }
    public int CandidateOrdinal { get; set; }
    public int Revision { get; set; } = 1;
    public Guid? InputRevisionId { get; set; }
    public Guid? ExtractionResultId { get; set; }
    public string State { get; set; } = "waiting";
    public bool RequiresExplicitRestoration { get; set; }
    public bool ManuallyCorrected { get; set; }
    public string CorrectionProvenanceJson { get; set; } = "{}";
    public string EffectiveJson { get; set; } = "{}";
    public string ReasonsJson { get; set; } = "[]";
    public string DuplicateDecision { get; set; } = "unresolved";
    public Guid? DuplicateSourceId { get; set; }
    public long? DuplicateEventId { get; set; }
    public int? DuplicateEventRevision { get; set; }
    public long? EventId { get; set; }
    public int? EventRevision { get; set; }
    public Guid? LastReviewId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
