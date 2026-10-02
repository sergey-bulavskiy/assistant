using System.Globalization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Result of one /threshold change: the updated rule, or a Russian error reply.</summary>
public sealed record SafetyRuleEdit(SafetyRuleInfo? Rule, string? Error);

/// <summary>Pure: applies "/threshold &lt;key&gt; &lt;field&gt; &lt;value&gt;" to a rule. Only the fields the
/// rule uses can change; a changed rule's source becomes doctor.</summary>
public static class SafetyRuleEditor
{
    public const string ValueErrorText = "Значение: число больше 0 и меньше 1000, не больше двух знаков после запятой.";
    public const string OrderErrorText = "Не сохранено: нужно low_urgent ≤ low_alert ≤ target_high ≤ high_alert ≤ high_urgent.";
    public const string LevelErrorText = "Уровень: alert или urgent.";
    public const string WindowErrorText = "Окно: целое число часов от 1 до 168.";

    public static SafetyRuleEdit SetField(SafetyRuleInfo current, string field, string rawValue)
    {
        var used = SafetyRuleFields.UsedBy(current);
        var name = field.Trim().ToLowerInvariant();
        if (!used.Contains(name))
        {
            return new SafetyRuleEdit(null, $"У правила {current.RuleKey} нет поля {field}. Поля: {string.Join(", ", used)}.");
        }

        var value = rawValue.Trim();
        SafetyRuleInfo updated;
        switch (name)
        {
            case SafetyRuleFields.SymptomLevel:
            {
                var level = value.ToLowerInvariant();
                if (level != SymptomLevels.Alert && level != SymptomLevels.Urgent)
                {
                    return new SafetyRuleEdit(null, LevelErrorText);
                }

                updated = current with { SymptomLevel = level };
                break;
            }

            case SafetyRuleFields.WindowHours:
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 168)
                {
                    return new SafetyRuleEdit(null, WindowErrorText);
                }

                updated = current with { WindowHours = hours };
                break;
            }

            default:
            {
                if (!decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                    || number <= 0 || number >= 1000 || decimal.Round(number, 2) != number)
                {
                    return new SafetyRuleEdit(null, ValueErrorText);
                }

                updated = name switch
                {
                    SafetyRuleFields.LowUrgent => current with { LowUrgent = number },
                    SafetyRuleFields.LowAlert => current with { LowAlert = number },
                    SafetyRuleFields.TargetHigh => current with { TargetHigh = number },
                    SafetyRuleFields.HighAlert => current with { HighAlert = number },
                    _ => current with { HighUrgent = number }
                };
                if (!IsOrdered(updated))
                {
                    return new SafetyRuleEdit(null, OrderErrorText);
                }

                break;
            }
        }

        return new SafetyRuleEdit(updated with { Source = SafetyRuleSources.Doctor }, null);
    }

    // Within one rule the thresholds must not cross, so a typo such as 1.1 for 11 is refused.
    private static bool IsOrdered(SafetyRuleInfo rule)
    {
        var values = new[] { rule.LowUrgent, rule.LowAlert, rule.TargetHigh, rule.HighAlert, rule.HighUrgent }
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
