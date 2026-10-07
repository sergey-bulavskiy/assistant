using System.Globalization;
using System.Text;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>What goes to the model for one extraction: roles/health/extract.md plus a runtime block
/// rendered by code (time zone, local now, the message's local send time, allowed values). No
/// profile values, names or notes ever go here.</summary>
public static class ExtractionPrompt
{
    public const int MinTextLength = 3;

    /// <summary>Ineligible text needs 3 characters; eligible new text can be shorter. Text without
    /// any letter or digit (including emoji only) never enters interpretation.</summary>
    public static bool ShouldExtract(string text, bool eligible = false)
    {
        var trimmed = text.Trim();
        if (!eligible && trimmed.Length < MinTextLength)
        {
            return false;
        }

        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildSystemPrompt(string instructions, DateTimeOffset utcNow, DateTimeOffset messageSentAt, string timeZoneId,
        bool isPrivate = false, bool isAddressed = false, bool replyToAll = false, bool isEdit = false)
    {
        var zone = ProfileTimeZone.Find(timeZoneId);
        var builder = new StringBuilder(instructions.TrimEnd());
        builder.Append("\n\n## Runtime\n\n");
        builder.Append("- Private chat: ").Append(isPrivate ? "true" : "false").Append('\n');
        builder.Append("- Explicitly addressed or private: ").Append(isAddressed ? "true" : "false").Append('\n');
        builder.Append("- This approved place permits replies without addressing: ").Append(replyToAll ? "true" : "false").Append('\n');
        builder.Append("- Edited message: ").Append(isEdit ? "true" : "false").Append('\n');
        builder.Append("- Time zone: ").Append(ProfileTimeZone.DisplayId(timeZoneId)).Append('\n');
        builder.Append("- Current local date and time: ").Append(Describe(TimeZoneInfo.ConvertTime(utcNow, zone))).Append('\n');
        builder.Append("- The message was sent at local time: ").Append(Describe(TimeZoneInfo.ConvertTime(messageSentAt, zone))).Append('\n');
        AppendList(builder, "Event types", HealthEventTypes.All);
        AppendList(builder, "glucose.context", GlucoseContexts.All);
        AppendList(builder, "glucose.unit", new[] { GlucoseUnits.MmolPerLiter, GlucoseUnits.MgPerDeciliter });
        AppendList(builder, "insulin.kind", InsulinKinds.All);
        AppendList(builder, "meal.meal_kind", MealKinds.All);
        AppendList(builder, "symptom.code", SymptomCodes.All);
        AppendList(builder, "unclear.reason", UnclearReasons.All);
        return builder.ToString();
    }

    private static string Describe(DateTimeOffset local) =>
        local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + local.ToString("dddd", CultureInfo.InvariantCulture) + ")";

    private static void AppendList(StringBuilder builder, string name, IEnumerable<string> values) =>
        builder.Append("- ").Append(name).Append(": ").Append(string.Join(", ", values)).Append('\n');
}
