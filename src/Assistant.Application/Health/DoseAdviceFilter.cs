using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Application.Health;

/// <summary>Enforces "never advise a medicine dose" on the only model text the health bot sends
/// (answers to questions). Pure and deterministic, tuned for recall: over-blocking is accepted, a
/// missed dose recommendation is not.
/// <para>The answer is first normalised: format characters (\p{Cf}: zero-width, direction and
/// isolate marks, soft hyphen, BOM) are removed, the text is put in Unicode form KC (full-width
/// letters and digits become plain ones) and combining marks (stress accents) are removed. An answer
/// that then still holds a letter outside Latin and Cyrillic is treated as dose advice (fail closed):
/// the patterns below only know Russian and English. Ill-formed text and a regex timeout count as dose
/// advice too.</para>
/// <para>The answer is split into sentences (and lines); each sentence is checked together with the
/// next one and with the heading line it sits under (a line ending with ':' heads the lines after it,
/// up to a blank line). A window is dose advice when it names a dose (a dose word, a medicine, or a
/// number followed by a dose unit) together with a change, intake or advice word, or when it gives a
/// number with a dose unit together with a timing word (a bare schedule: "6 ед перед ужином"). Then
/// the whole answer is replaced by RefusalText.</para>
/// <para>Lookalike letters: besides the normalised text, two copies are checked where, in every word
/// that mixes Latin and Cyrillic letters, the Latin lookalikes (a e o p c x y k m t h b) are mapped to
/// the Cyrillic ones, and the other way round. A match in any copy is dose advice.</para>
/// <para>Known costs and limits: an answer that repeats a recorded insulin entry with its time of day
/// ("вы записали 6 ед перед ужином") is refused; Russian written in Latin letters (transliteration,
/// "uvelichte dozu") is not recognised.</para></summary>
public static class DoseAdviceFilter
{
    public const string RefusalText =
        "Я не даю советов по дозам лекарств. Это вопрос к врачу — запишите его, чтобы спросить на приёме.";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Format characters (soft hyphen, zero-width, direction and isolate marks, word joiner, BOM, …) and,
    // after form KC, combining marks: removed so they cannot split a word.
    private const string FormatPattern = @"\p{Cf}+";

    private const string MarkPattern = @"\p{Mn}+";

    // A letter outside Latin and Cyrillic. Matched without IgnoreCase (case folding is not needed and
    // must not widen the subtracted blocks).
    private const string ForeignLetterPattern =
        @"[\p{L}-[\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsCyrillic}]]";

    private const string LineBreakPattern = @"\r\n|[\r\n  ]";

    // Inside a line a sentence ends at . ! ? … or ; followed by whitespace ("7.8" is not split), unless
    // a lower-case letter follows: "6 ед. перед ужином" stays one sentence.
    private const string SentenceBreakPattern = @"(?<=[.!?…;])\s+(?-i:(?![a-zа-яё]))";

    // Word starts, except where a word end is given. A number followed by a dose unit counts too;
    // мг/дл and mg/dL are glucose units, not doses, but мг/сут or mg/day are doses.
    private const string DosePattern =
        @"\b(?:доз|единиц|единич|инсулин|препарат|лекарств|медикамент|таблет|капсул|пилюл|укол|инъекц|ампул|шприц" +
        @"|пролонг|болюс|базал|витамин|миллиграм|коротк(?:ий|ого|ому|им|ом)\b|длинн(?:ый|ого|ому|ым|ом)\b" +
        @"|продл[её]нн|ед(?![а-яё])|ме(?![а-яё]))" +
        @"|\b(?:dos(?:e|es|ed|age|ages|ing)\b|insulin|medic(?:ine|ines|ation|ations)\b|meds\b|drugs?\b|pills?\b" +
        @"|tablets?\b|capsules?\b|units?\b|shots?\b|jabs?\b|injections?\b|bolus|basal|vitamin" +
        @"|(?:short|long|rapid|fast)[- ]acting)" +
        @"|\d+(?:[.,]\d+)?\s*(?:ед(?![а-яё])|ме(?![а-яё])|мкг(?![а-яё])|мг(?![а-яё])(?!\s*/\s*дл)" +
        @"|iu\b|u\b|mcg\b|mg\b(?!\s*/\s*dl))";

    // Change, intake and advice words (word starts, except where a word end is given).
    private const string ChangePattern =
        @"\b(?:увелич|уменьш|сниз|сниж|пониз|пониж|повыс|повыш|подня|подни|добав|прибав|убав|сбав|убер|убра" +
        @"|сократ|сокращ|удво|удва|вдвое|наполовину|раздел|измен|меня(?:й|ть|ет|ют|ем|ете)|поменя|смен|замен" +
        @"|скоррект|подкоррект|коррект|пересмотр|отмен|отказ|прекрат|переста|пропус|перенес|перенос|сдвин" +
        @"|вве|ввод|вколи|уколи|вколо|уколо|колите|кольте|колоть|коли(?![а-яё])|подкол|докол" +
        @"|прими|приним|приня|выпей|выпить|выпива|пейте|пей(?![а-яё])|пить|сдела|делай|делать|постав|став[ьи]" +
        @"|продолж|остав|начн|начин|нача(?:ть|л)|плюс|минус|больше|меньше|побольш|поменьш|лишн|дополнит" +
        @"|ещё|еще|достаточн|хватит|нужн|надо|следует|стоит|рекоменд|лучше|должн|можно|попробу)" +
        @"|\b(?:increas|decreas|rais|lower|reduc|adjust|doubl|halv|split|skip|cut|add|take|took|inject|chang" +
        @"|switch|swap|stop|start|continu|keep|give|us(?:e|es|ed|ing)\b|bump|drop|up\b|down\b|extra|more|less" +
        @"|fewer|enough|should|need|recommend|try|better)";

    // A bare schedule: a number with a dose unit ...
    private const string AmountPattern =
        @"\d+(?:[.,]\d+)?\s*(?:ед(?![а-яё])|единиц|ме(?![а-яё])|мкг(?![а-яё])|мг(?![а-яё])(?!\s*/\s*дл)|таблет|капсул" +
        @"|iu\b|u\b|units?\b|mcg\b|mg\b(?!\s*/\s*dl)|tablets?\b|pills?\b|capsules?\b)";

    // ... together with a timing word (word starts).
    private const string TimingPattern =
        @"\b(?:перед|после|утр|вечер|ноч|дн[её]м|завтрак|обед|ужин)" +
        @"|\b(?:before|after|bedtime|morning|evening|night|breakfast|lunch|dinner|supper|meals?\b)";

    private const string LatinLookalikes = "aeopcxykmthbAEOPCXYKMTHB";

    private const string CyrillicLookalikes = "аеорсхукмтнвАЕОРСХУКМТНВ";

    private static readonly Matchers Default = new(MatchTimeout);

    public static bool ContainsDoseAdvice(string answer) => ContainsDoseAdvice(answer, MatchTimeout);

    /// <summary>As ContainsDoseAdvice(answer) with a given regex timeout (tests use a tiny one to show
    /// that a timeout counts as dose advice).</summary>
    public static bool ContainsDoseAdvice(string answer, TimeSpan matchTimeout)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var matchers = matchTimeout == MatchTimeout ? Default : new Matchers(matchTimeout);
        try
        {
            var text = matchers.Format.Replace(answer, string.Empty).Normalize(NormalizationForm.FormKC);
            text = matchers.Mark.Replace(text, string.Empty);
            if (matchers.ForeignLetter.IsMatch(text))
            {
                return true;
            }

            if (WindowsContainDoseAdvice(text, matchers))
            {
                return true;
            }

            var cyrillic = MapMixedWords(text, LatinLookalikes, CyrillicLookalikes);
            if (cyrillic != text && WindowsContainDoseAdvice(cyrillic, matchers))
            {
                return true;
            }

            var latin = MapMixedWords(text, CyrillicLookalikes, LatinLookalikes);
            return latin != text && WindowsContainDoseAdvice(latin, matchers);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            // Normalize throws on ill-formed UTF-16 (a lone surrogate).
            return true;
        }
    }

    private static bool WindowsContainDoseAdvice(string text, Matchers matchers)
    {
        var sentences = Sentences(text, matchers);
        for (var i = 0; i < sentences.Count; i++)
        {
            var window = $"{sentences[i].Heading} {sentences[i].Text}";
            if (i + 1 < sentences.Count)
            {
                window += " " + sentences[i + 1].Text;
            }

            if ((matchers.Dose.IsMatch(window) && matchers.Change.IsMatch(window))
                || (matchers.Amount.IsMatch(window) && matchers.Timing.IsMatch(window)))
            {
                return true;
            }
        }

        return false;
    }

    // In every word (a run of letters and digits) that holds both Latin and Cyrillic letters, replaces
    // each character found in `from` by the one at the same index in `to`.
    private static string MapMixedWords(string text, string from, string to)
    {
        var result = new StringBuilder(text.Length);
        var start = 0;
        while (start < text.Length)
        {
            if (!char.IsLetterOrDigit(text[start]))
            {
                result.Append(text[start]);
                start++;
                continue;
            }

            var end = start;
            var hasLatin = false;
            var hasCyrillic = false;
            while (end < text.Length && char.IsLetterOrDigit(text[end]))
            {
                hasLatin |= text[end] is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
                hasCyrillic |= text[end] is >= '\u0400' and <= '\u04FF';
                end++;
            }

            for (var i = start; i < end; i++)
            {
                var index = hasLatin && hasCyrillic ? from.IndexOf(text[i], StringComparison.Ordinal) : -1;
                result.Append(index >= 0 ? to[index] : text[i]);
            }

            start = end;
        }

        return result.ToString();
    }

    /// <summary>The answer unchanged, or RefusalText when any part of it looks like dose advice.</summary>
    public static string Apply(string answer) => ContainsDoseAdvice(answer) ? RefusalText : answer;

    private static List<(string Heading, string Text)> Sentences(string text, Matchers matchers)
    {
        var sentences = new List<(string Heading, string Text)>();
        var heading = string.Empty;
        foreach (var rawLine in matchers.LineBreak.Split(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                heading = string.Empty;
                continue;
            }

            foreach (var part in matchers.SentenceBreak.Split(line))
            {
                var sentence = part.Trim();
                if (sentence.Length > 0)
                {
                    sentences.Add((heading, sentence));
                }
            }

            if (line.EndsWith(':'))
            {
                heading = line;
            }
        }

        return sentences;
    }

    private sealed class Matchers(TimeSpan timeout)
    {
        public Regex Format { get; } = new(FormatPattern, Options, timeout);

        public Regex Mark { get; } = new(MarkPattern, Options, timeout);

        public Regex ForeignLetter { get; } = new(ForeignLetterPattern, RegexOptions.CultureInvariant, timeout);

        public Regex Amount { get; } = new(AmountPattern, Options, timeout);

        public Regex Timing { get; } = new(TimingPattern, Options, timeout);

        public Regex LineBreak { get; } = new(LineBreakPattern, Options, timeout);

        public Regex SentenceBreak { get; } = new(SentenceBreakPattern, Options, timeout);

        public Regex Dose { get; } = new(DosePattern, Options, timeout);

        public Regex Change { get; } = new(ChangePattern, Options, timeout);
    }
}
