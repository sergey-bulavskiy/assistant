using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Safety net next to the model: finds the reading formats it knows and nothing else.
/// Glucose: a keyword (сахар…, глюкоз…, глюкометр…) followed within 30 characters by a number of
/// up to three digits with at most two decimals ("2.55", "250"). The gap holds no digit, no
/// sentence end (. ! ? ; or a line break) and no other measurement word (вес, давлен…, пульс,
/// инсулин); a number followed by a time, weight or dose unit, or by "/", is not glucose. Blood
/// pressure: "120/80" or "120 на 80". Hits are not validated (a three-digit glucose number is most
/// likely another unit): the caller runs them through HealthEventValidator and the safety rules.
/// Never records anything.</summary>
public static class QuickReadingScanner
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex GlucosePattern = new(
        @"(?:сахар|глюкоз|глюкометр)[а-яё]*" +
        @"(?:(?!вес|давлен|пульс|инсулин)[^\d.!?;\n]){0,30}?" +
        @"(?<value>\d{1,3}(?:[.,]\d{1,2})?)(?![.,]?\d)" +
        @"(?!\s*(?:[/%]|(?:час[а-яё]*|ч|мин[а-яё]*|раз|кг|г|гр|грамм[а-яё]*|ед|единиц[а-яё]*|шт)(?![а-яё])))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex PressurePattern = new(
        @"(?<!\d)(?<sys>\d{2,3})\s*(?:/|на)\s*(?<dia>\d{2,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Glucose hits first, then pressure hits, each in text order.</summary>
    public static IReadOnlyList<ExtractedEvent> Scan(string text)
    {
        var hits = new List<ExtractedEvent>();
        try
        {
            foreach (Match match in GlucosePattern.Matches(text))
            {
                hits.Add(new ExtractedEvent
                {
                    Type = HealthEventTypes.Glucose,
                    Value = Parse(match.Groups["value"].Value),
                    Context = GlucoseContexts.Other
                });
            }

            foreach (Match match in PressurePattern.Matches(text))
            {
                hits.Add(new ExtractedEvent
                {
                    Type = HealthEventTypes.BloodPressure,
                    Systolic = Parse(match.Groups["sys"].Value),
                    Diastolic = Parse(match.Groups["dia"].Value)
                });
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: keep what was found so far.
        }

        return hits;
    }

    private static decimal Parse(string number) =>
        decimal.Parse(number.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
