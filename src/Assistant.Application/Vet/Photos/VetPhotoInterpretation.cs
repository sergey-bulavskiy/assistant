using System.Text;
using System.Text.Json;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoDisplay(string ValueText, decimal? NumericValue, string? Unit,
    int? Year, bool YearDisplayed, int? Month, int? Day, string? Time, string? Offset);
public sealed record VetPhotoInterpretation(Guid PhotoSourceId, Guid InputRevisionId, string Kind,
    IReadOnlyList<VetPhotoDisplay> Displays, IReadOnlyList<string> Reasons, string? Notes);

public static class VetPhotoInterpretationParser
{
    public const int MaxResultBytes = 16_384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RootFields = ["schema_version", "photo_source_id", "input_revision_id", "kind", "displays", "reasons", "notes"];
    private static readonly string[] DisplayFields = ["value_text", "decimal_value", "unit", "year", "year_displayed", "month", "day", "time", "offset"];

    public static VetPhotoInterpretation? Parse(string json, Guid expectedSourceId, Guid expectedInputId)
    {
        if (expectedSourceId == Guid.Empty || expectedInputId == Guid.Empty || json.Length > MaxResultBytes) return null;
        try
        {
            if (StrictUtf8.GetByteCount(json) > MaxResultBytes) return null;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (!Closed(root, RootFields) || !root.GetProperty("schema_version").TryGetInt32(out var version) || version != 1
                || Identity(root, "photo_source_id") != expectedSourceId || Identity(root, "input_revision_id") != expectedInputId) return null;
            var kind = Text(root, "kind", 30);
            if (kind is not ("meter" or "unreadable" or "unsupported")) return null;
            var rawDisplays = root.GetProperty("displays");
            if (rawDisplays.ValueKind != JsonValueKind.Array || rawDisplays.GetArrayLength() > 8) return null;
            var displays = new List<VetPhotoDisplay>();
            foreach (var d in rawDisplays.EnumerateArray())
            {
                if (!Closed(d, DisplayFields)) return null;
                var visible = Text(d, "value_text", 100);
                if (visible is null || visible.Length == 0) return null;
                var numeric = d.GetProperty("decimal_value");
                decimal? value = null;
                if (numeric.ValueKind != JsonValueKind.Null)
                {
                    if (numeric.ValueKind != JsonValueKind.Number
                        || !VetInterpretationParser.TryPositiveDecimal(numeric.GetRawText(), out var parsed)
                        || !VetInterpretationParser.TryPositiveDecimal(visible, out var visibleValue) || parsed != visibleValue) return null;
                    value = parsed;
                }
                var year = Integer(d, "year", 1, 9999);
                var displayed = d.GetProperty("year_displayed");
                if (displayed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                if (displayed.GetBoolean() != (year is not null)) return null;
                displays.Add(new(visible, value, Text(d, "unit", 40), year, displayed.GetBoolean(),
                    Integer(d, "month", 1, 12), Integer(d, "day", 1, 31), Text(d, "time", 8), Text(d, "offset", 6)));
            }
            if (kind != "meter" && displays.Count != 0) return null;
            var rawReasons = root.GetProperty("reasons");
            if (rawReasons.ValueKind != JsonValueKind.Array || rawReasons.GetArrayLength() > 8) return null;
            var reasons = new List<string>();
            foreach (var r in rawReasons.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String) return null;
                var reason = r.GetString()!;
                if (!SafeText(reason, 500) || reason.Length == 0) return null;
                reasons.Add(reason);
            }
            return new(expectedSourceId, expectedInputId, kind, displays, reasons, Text(root, "notes", 500));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
        catch (EncoderFallbackException) { return null; }
    }

    private static bool Closed(JsonElement e, string[] fields) => e.ValueKind == JsonValueKind.Object
        && e.EnumerateObject().Count() == fields.Length
        && e.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal))
        && e.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length;
    private static Guid Identity(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String && Guid.TryParseExact(root.GetProperty(name).GetString(), "D", out var id)
            ? id : throw new JsonException();
    private static int? Integer(JsonElement e, string name, int min, int max)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max
            ? n : throw new JsonException();
    }
    private static string? Text(JsonElement e, string name, int max)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && SafeText(value.GetString()!, max)
            ? value.GetString() : throw new JsonException();
    }
    private static bool SafeText(string text, int max)
    {
        if (text.Length > max || text.Contains('\0')) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
}
