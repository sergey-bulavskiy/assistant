namespace Assistant.Application.Vet.Photos;

/// <summary>Conservative current-turn request grounding; references never authorize an action.</summary>
public static class VetPhotoActionEvidence
{
    public static bool Matches(VetPhotoOperation operation, string sourceText)
    {
        var evidence = operation.ActionEvidence;
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 500) return false;
        var request = sourceText.TrimStart();
        if (!request.StartsWith(evidence, StringComparison.Ordinal)) return false;
        var phrases = operation.Kind switch
        {
            "correct" => new[] { "исправь", "скорректируй", "замени", "поменяй", "correct", "change", "replace" },
            "start" => ["начни", "начать", "start"],
            "close" => ["закончи", "готово", "заверши", "done", "finish"],
            "show" => ["покажи", "show"],
            "review" => ["проверь", "review"],
            "exclude" => ["исключи", "exclude"],
            "save" => ["сохрани", "save"],
            "cancel" => ["отмени", "cancel"],
            "assumptions" => ["установи", "задай", "set"],
            "undo" => ["отмени", "undo"],
            "reverse" => ["отмени", "reverse"],
            "reprocess" => ["перечитай", "перераспознай", "reprocess"],
            "delete_originals" => ["удали", "delete"],
            "accept" => ["да", "подтверждаю", "yes", "confirm"],
            "decline" => ["нет", "отклоняю", "no", "decline"],
            "add_late" => ["добавь", "add"],
            "duplicate" => ["считай", "consider"],
            "continue" => ["продолжи", "continue"],
            _ => []
        };
        return phrases.Any(phrase => evidence.StartsWith(phrase, StringComparison.OrdinalIgnoreCase)
            && (evidence.Length == phrase.Length || !char.IsLetterOrDigit(evidence[phrase.Length]))
            && (request.Length == phrase.Length || !char.IsLetterOrDigit(request[phrase.Length])));
    }
}
