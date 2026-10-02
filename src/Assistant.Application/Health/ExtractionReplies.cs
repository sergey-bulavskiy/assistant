namespace Assistant.Application.Health;

/// <summary>Fixed texts of the extraction path. Never model text.</summary>
public static class ExtractionReplies
{
    public const string FailureNotice =
        "⚠️ Не смог обработать сообщение — ничего не записано. Повторите позже. " +
        "Если значение опасное, не ждите бота: свяжитесь с врачом.";

    public const string GenericClarification = "Не понял одно из значений — уточните, пожалуйста.";

    public const int MaxFragmentLength = 30;

    /// <summary>"Не понял «18» — уточните единицы (нужно в ммоль/л)." The fragment is shown only when
    /// it is literally in the message (or its comma form is), at most 30 characters.</summary>
    public static string Clarification(ExtractedUnclear problem, string messageText)
    {
        var fragment = LiteralFragment(problem.Fragment, messageText);
        return fragment is null
            ? GenericClarification
            : $"Не понял «{fragment}» — уточните {WhatToClarify(problem.Reason)}.";
    }

    private static string WhatToClarify(string? reason) => reason switch
    {
        UnclearReasons.Unit => "единицы (нужно в ммоль/л)",
        UnclearReasons.Time => "время",
        UnclearReasons.Type => "что это за показатель",
        _ => "значение"
    };

    private static string? LiteralFragment(string? fragment, string messageText)
    {
        var candidate = fragment?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxFragmentLength)
        {
            return null;
        }

        if (messageText.Contains(candidate, StringComparison.Ordinal))
        {
            return candidate;
        }

        var commaForm = candidate.Replace('.', ',');
        return commaForm != candidate && messageText.Contains(commaForm, StringComparison.Ordinal) ? commaForm : null;
    }
}
