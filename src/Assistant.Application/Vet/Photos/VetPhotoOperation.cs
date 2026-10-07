using System.Globalization;
using System.Text.Json;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoOperationAssumptions(int? Year, string? TimeZone, string? GlucoseUnit);
public sealed record VetPhotoOperationCorrection(Guid? SourceId, int? TargetCandidateRevision,
    string? Value, string? Unit, string? Date, string? Time, string? Offset, bool RestoreRequested);
public sealed record VetPhotoOperation(string Kind, Guid? BatchId, Guid? ReviewId, Guid? RunId,
    IReadOnlyList<Guid> SourceIds, IReadOnlyList<Guid> CandidateIds, int? ReviewRevision,
    IReadOnlyList<int> ItemNumbers, long? ActionId, long? TargetEventId, string? SelectionMode,
    string? FromDate, string? UntilDate, string? DateAxis, string? DuplicateChoice,
    VetPhotoOperationAssumptions? Assumptions, IReadOnlyList<VetPhotoOperationCorrection> Corrections);

public static class VetPhotoOperationParser
{
    private static readonly string[] Fields = ["kind", "batch_id", "review_id", "run_id", "source_ids", "candidate_ids",
        "review_revision", "item_numbers", "action_id", "target_event_id", "selection_mode", "from_date", "until_date",
        "date_axis", "duplicate_choice", "assumptions", "corrections"];
    private static readonly string[] Kinds = ["start", "close", "show", "review", "correct", "exclude", "save", "cancel",
        "assumptions", "undo", "reverse", "reprocess", "delete_originals", "accept", "decline", "add_late", "duplicate", "continue"];

    public static VetPhotoOperation? Parse(JsonElement root)
    {
        try
        {
            if (!Closed(root, Fields)) return null;
            var kind = Text(root, "kind", 30);
            if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal)) return null;
            var batch = Id(root, "batch_id"); var review = Id(root, "review_id"); var run = Id(root, "run_id");
            var sources = Ids(root, "source_ids"); var candidates = Ids(root, "candidate_ids");
            var revision = Integer(root, "review_revision", int.MaxValue);
            var items = new List<int>();
            if (Optional(root, "item_numbers") is { } itemArray)
            {
                if (itemArray.ValueKind != JsonValueKind.Array || itemArray.GetArrayLength() > 50) return null;
                foreach (var item in itemArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var n) || n is < 1 or > 50) return null;
                    items.Add(n);
                }
                if (items.Distinct().Count() != items.Count) return null;
            }
            var from = Date(root, "from_date"); var until = Date(root, "until_date");
            if (from is not null && until is not null && string.CompareOrdinal(from, until) > 0) return null;
            var mode = Choice(root, "selection_mode", ["current", "all_originals", "selected"]);
            var axis = Choice(root, "date_axis", ["measurement", "upload"]);
            var duplicate = Choice(root, "duplicate_choice", ["same", "separate", "exclude"]);
            VetPhotoOperationAssumptions? assumptions = null;
            if (Optional(root, "assumptions") is { } a)
            {
                if (!Closed(a, ["year", "time_zone", "glucose_unit"])) return null;
                assumptions = new(Integer(a, "year", 9999), Text(a, "time_zone", 256), Text(a, "glucose_unit", 40));
            }
            var corrections = new List<VetPhotoOperationCorrection>();
            if (Optional(root, "corrections") is { } changes)
            {
                if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() > 50) return null;
                foreach (var c in changes.EnumerateArray())
                {
                    if (!Closed(c, ["source_id", "target_candidate_revision", "value", "unit", "date", "time", "offset", "restore_requested"])) return null;
                    var value = Text(c, "value", 100);
                    if (value is not null && !VetInterpretationParser.TryPositiveDecimal(value, out _)) return null;
                    var time = Text(c, "time", 8);
                    if (time is not null && !TimeOnly.TryParseExact(time, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
                    var offset = Text(c, "offset", 6);
                    if (offset is not null && !Offset(offset)) return null;
                    var restore = Optional(c, "restore_requested");
                    if (restore is { } r && r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                    corrections.Add(new(Id(c, "source_id"), Integer(c, "target_candidate_revision", int.MaxValue), value,
                        Text(c, "unit", 40), Date(c, "date"), time, offset, restore?.ValueKind == JsonValueKind.True));
                }
                var targeted = corrections.Where(c => c.SourceId != null).Select(c => c.SourceId);
                if (targeted.Distinct().Count() != targeted.Count()) return null;
            }
            return new(kind, batch, review, run, sources, candidates, revision, items.AsReadOnly(),
                Long(root, "action_id"), Long(root, "target_event_id"), mode, from, until, axis, duplicate,
                assumptions, corrections.AsReadOnly());
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static bool Closed(JsonElement e, string[] allowed) => e.ValueKind == JsonValueKind.Object
        && e.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal))
        && e.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == e.EnumerateObject().Count();
    private static JsonElement? Optional(JsonElement e, string field) =>
        !e.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null : value;
    private static string? Text(JsonElement e, string field, int max)
    {
        var value = Optional(e, field);
        if (value is null) return null;
        if (value.Value.ValueKind != JsonValueKind.String) throw new JsonException();
        var text = value.Value.GetString()!;
        if (text.Length > max || text.Contains('\0')) throw new JsonException();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) throw new JsonException();
            }
            else if (char.IsLowSurrogate(text[i])) throw new JsonException();
        }
        return text;
    }
    private static Guid? Id(JsonElement e, string field)
    {
        var raw = Text(e, field, 36);
        return raw is null ? null : Guid.TryParseExact(raw, "D", out var id) && id != Guid.Empty ? id : throw new JsonException();
    }
    private static IReadOnlyList<Guid> Ids(JsonElement e, string field)
    {
        var list = new List<Guid>();
        if (Optional(e, field) is not { } value) return list.AsReadOnly();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 50) throw new JsonException();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: 36 } raw
                || !Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty)
                throw new JsonException();
            list.Add(id);
        }
        if (list.Distinct().Count() != list.Count) throw new JsonException();
        return list.AsReadOnly();
    }
    private static int? Integer(JsonElement e, string field, int max)
    {
        var value = Optional(e, field);
        if (value is null) return null;
        return value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var n) && n > 0 && n <= max
            ? n : throw new JsonException();
    }
    private static long? Long(JsonElement e, string field)
    {
        var value = Optional(e, field);
        if (value is null) return null;
        return value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var n) && n > 0
            ? n : throw new JsonException();
    }
    private static string? Choice(JsonElement e, string field, string[] allowed)
    {
        var raw = Text(e, field, 30);
        return raw is null || allowed.Contains(raw, StringComparer.Ordinal) ? raw : throw new JsonException();
    }
    private static string? Date(JsonElement e, string field)
    {
        var raw = Text(e, field, 10);
        return raw is null || DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? raw : throw new JsonException();
    }
    private static bool Offset(string raw)
    {
        return raw.Length == 6 && (raw[0] is '+' or '-') && raw[3] == ':'
            && int.TryParse(raw.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(raw.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && hours <= 14 && minutes <= 59 && (hours != 14 || minutes == 0);
    }
}
