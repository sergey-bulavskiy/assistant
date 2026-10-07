using System.Globalization;
using System.Text.Json;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoCaptionInput(string Intent, VetPhotoContext Context);
public static class VetPhotoCaptionInputParser
{
    public static VetPhotoCaptionInput? Parse(JsonElement input)
    {
        string[] fields = ["intent", "value", "unit", "year", "month", "day", "time", "offset"];
        try
        {
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Count() != fields.Length
                || input.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))
                || input.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                return null;
            var intent = Text(input, "intent", 30);
            if (intent is not ("record" or "question_only" or "unsure")) return null;
            var value = Text(input, "value", 100);
            if (value != null && !VetInterpretationParser.TryPositiveDecimal(value, out _)) return null;
            var time = Text(input, "time", 8);
            if (time != null && !TimeOnly.TryParseExact(time, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) return null;
            var offset = Text(input, "offset", 6);
            if (offset != null && (offset.Length != 6 || offset[0] is not ('+' or '-') || offset[3] != ':'
                || !int.TryParse(offset.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(offset.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                || hours > 14 || minutes > 59 || hours == 14 && minutes != 0)) return null;
            return new(intent, new(value, Text(input, "unit", 40), Integer(input, "year", 9999),
                Integer(input, "month", 12), Integer(input, "day", 31), time, offset));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
    private static int? Integer(JsonElement input, string name, int max)
    {
        var value = input.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result) && result is > 0 && result <= max ? result : throw new JsonException();
    }
    private static string? Text(JsonElement input, string name, int max)
    {
        var value = input.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > max || text.Contains('\0'))
            throw new JsonException();
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw new JsonException(); }
            else if (char.IsLowSurrogate(text[index])) throw new JsonException();
        }
        return text;
    }
}
