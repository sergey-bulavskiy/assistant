namespace Assistant.Application.Health;

/// <summary>One event as the model wrote it (JSON contract of roles/health/extract.md), before
/// validation. Every field is optional; HealthEventValidator decides what can be recorded.</summary>
public sealed record ExtractedEvent
{
    public string? Type { get; init; }

    /// <summary>An ExtractionIntents value. ExtractionParser always sets it (ExtractionIntents.Resolve).</summary>
    public string? Intent { get; init; }

    public int? Day { get; init; }
    public string? Time { get; init; }
    public decimal? Value { get; init; }
    public string? Unit { get; init; }
    public string? Context { get; init; }
    public decimal? Units { get; init; }
    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? MealKind { get; init; }
    public string? Description { get; init; }
    public string? Code { get; init; }
    public string? Text { get; init; }
    public string[]? Tags { get; init; }
    public decimal? Kg { get; init; }
    public decimal? Systolic { get; init; }
    public decimal? Diastolic { get; init; }
    public decimal? Pulse { get; init; }
}

/// <summary>Something that looks like a reading but cannot be recorded. Reason is one of
/// UnclearReasons (anything else is treated as "value").</summary>
public sealed record ExtractedUnclear
{
    public string? Fragment { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A parsed extraction answer (known event types only). Undo: the message asks to remove a
/// recording or not to keep it (the model decides; code limits what is removed).</summary>
public sealed record ExtractionOutput(
    IReadOnlyList<ExtractedEvent> Events, IReadOnlyList<ExtractedUnclear> Unclear, bool IsQuestion, bool Undo = false, bool NeedsReply = false);
