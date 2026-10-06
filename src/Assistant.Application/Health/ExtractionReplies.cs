using System.Text.RegularExpressions;

namespace Assistant.Application.Health;

/// <summary>Fixed texts of the extraction path. Never model text.</summary>
public static class ExtractionReplies
{
    private const string NumberPattern = @"(?<![\p{L}\d.,])\d+(?:[.,]\d+)?(?![\p{L}\d]|[.,]\d)";
    private static readonly Regex Numbers = new(NumberPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Slash can mean blood pressure; minus signs can mean a reading range. Preserve both.
    private static readonly Regex Arithmetic = new(
        @"(?=(?<operand>" + NumberPattern + @")\s*[+*×÷]\s*(?<operand>" + NumberPattern + @"))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Unknown-type numbers used exclusively as arithmetic operands are not readings.
    /// Preserve ambiguity when that number also appears elsewhere; keep every other problem.</summary>
    public static bool ShouldClarify(ExtractedUnclear problem, string messageText)
    {
        if (problem.Reason != UnclearReasons.Type || string.IsNullOrWhiteSpace(problem.Fragment))
        {
            return true;
        }

        var fragment = problem.Fragment.Trim().Replace(',', '.');
        try
        {
            var occurrences = Numbers.Matches(messageText).Cast<Match>()
                .Where(m => m.Value.Replace(',', '.') == fragment).ToArray();
            if (occurrences.Length == 0)
            {
                return true;
            }

            var operands = Arithmetic.Matches(messageText).Cast<Match>()
                .SelectMany(m => m.Groups["operand"].Captures.Cast<Capture>())
                .Select(c => (c.Index, c.Length)).ToHashSet();
            return occurrences.Any(m => !operands.Contains((m.Index, m.Length)));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

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
