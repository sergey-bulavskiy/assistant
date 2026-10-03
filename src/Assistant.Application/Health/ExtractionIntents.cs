namespace Assistant.Application.Health;

/// <summary>What the person meant by one value: the "intent" field of roles/health/extract.md. The
/// model decides; code only acts on it (record → saved, question_only → never saved, unsure → asked
/// with Да/Нет). Safety rules run on every valid value whatever its intent.</summary>
public static class ExtractionIntents
{
    /// <summary>A value the person reports (measured, noticed, taken).</summary>
    public const string Record = "record";

    /// <summary>The number is only part of a question or a hypothetical.</summary>
    public const string QuestionOnly = "question_only";

    /// <summary>It could be either.</summary>
    public const string Unsure = "unsure";

    public static IReadOnlyList<string> All { get; } = new[] { Record, QuestionOnly, Unsure };

    /// <summary>The model's value when it is one of the three (trimmed, any case). A missing or unknown
    /// value becomes unsure in a message the model marked as a question and record otherwise, so a
    /// dropped field never stores a question's value silently and never loses a plain reading.</summary>
    public static string Resolve(string? intent, bool isQuestion)
    {
        var normalized = intent?.Trim().ToLowerInvariant();
        if (normalized is not null && All.Contains(normalized))
        {
            return normalized;
        }

        return isQuestion ? Unsure : Record;
    }
}
