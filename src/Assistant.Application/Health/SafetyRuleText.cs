using System.Globalization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>How a safety rule is shown in /thresholds and /threshold replies, e.g.
/// "glucose.any: low_urgent 3.0, low_alert 3.9, high_alert 11.0, high_urgent 13.9 — не подтверждено врачом".</summary>
public static class SafetyRuleText
{
    public const string DoctorLabel = "врач";
    public const string DefaultLabel = "не подтверждено врачом";

    public static string SourceLabel(string source) =>
        source == SafetyRuleSources.Doctor ? DoctorLabel : DefaultLabel;

    public static string Format(SafetyRuleInfo rule)
    {
        // Glucose keeps one decimal ("3.0", "11.0"); everything else drops trailing zeros ("140").
        // Doctor values may carry up to two decimals.
        var numberFormat = rule.RuleKey.StartsWith("glucose.", StringComparison.Ordinal) ? "0.0#" : "0.##";
        var parts = new List<string>();
        AddNumber(parts, SafetyRuleFields.LowUrgent, rule.LowUrgent, numberFormat);
        AddNumber(parts, SafetyRuleFields.LowAlert, rule.LowAlert, numberFormat);
        AddNumber(parts, SafetyRuleFields.TargetHigh, rule.TargetHigh, numberFormat);
        AddNumber(parts, SafetyRuleFields.HighAlert, rule.HighAlert, numberFormat);
        AddNumber(parts, SafetyRuleFields.HighUrgent, rule.HighUrgent, numberFormat);
        if (rule.SymptomLevel is { } level)
        {
            parts.Add($"{SafetyRuleFields.SymptomLevel} {level}");
        }

        if (rule.WindowHours is { } hours)
        {
            parts.Add($"{SafetyRuleFields.WindowHours} {hours.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"{rule.RuleKey}: {string.Join(", ", parts)} — {SourceLabel(rule.Source)}";
    }

    private static void AddNumber(List<string> parts, string field, decimal? value, string format)
    {
        if (value is { } number)
        {
            parts.Add($"{field} {number.ToString(format, CultureInfo.InvariantCulture)}");
        }
    }
}
