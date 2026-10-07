using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Application.Vet.Photos;

namespace Assistant.Application.Vet;

public sealed record VetCandidate(string EventType, string Intent, string? RawValue, string? Unit,
    string? Product, string? Date, string? Time, string? Offset, string TimeEvidence,
    int Ordinal, long? EventId = null, string? Unresolved = null);
public sealed record VetOperation(string Kind, long? EventId, IReadOnlyList<long> EventIds,
    long? PendingId, int? ReviewRevision, IReadOnlyList<VetProfileChange> ProfileChanges);
public sealed record VetHistoryQuery(string? FromDate, string? UntilDate, int Offset, bool Analysis);
public sealed record VetInterpretation(bool NeedsReply, IReadOnlyList<VetCandidate> Events,
    IReadOnlyList<string> Unclear, VetOperation? Operation, VetHistoryQuery? HistoryQuery)
{
    public VetPhotoOperation? PhotoOperation { get; init; }
    public VetPhotoCaptionInput? PhotoCaption { get; init; }
}
public sealed record VetValidation(VetCandidate Candidate, VetEventState? State, string? Reason);

public static class VetInterpretationParser
{
    private static readonly HashSet<string> RootFields = ["needs_reply", "events", "unclear", "operation", "history_query", "photo_operation", "photo_caption"];
    private static readonly HashSet<string> EventFields = ["type", "intent", "value", "dose", "unit", "product",
        "date", "time", "offset", "time_evidence", "ordinal", "event_id"];
    private static readonly HashSet<string> OperationFields = ["kind", "event_id", "event_ids", "pending_id", "review_revision", "profile_changes"];
    private static readonly HashSet<string> HistoryFields = ["from_date", "until_date", "offset", "analysis"];
    private static readonly HashSet<string> Kinds = ["accept", "decline", "correct", "delete", "profile", "undo", "continue", "retry"];

    public static VetInterpretation? Parse(string json)
    {
        if (json.Length > 64000) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (!Closed(root, RootFields)) return null;
            var reply = root.TryGetProperty("needs_reply", out var needs) && needs.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 20)
                return null;
            var candidates = new List<VetCandidate>();
            var ordinals = new Dictionary<string, int>();
            foreach (var e in events.EnumerateArray())
            {
                if (!Closed(e, EventFields)) return null;
                var type = String(e, "type", 30) ?? "unknown";
                var intent = String(e, "intent", 30);
                if (intent is not ("record" or "question_only" or "unsure")) intent = "unsure";
                var ordinal = ordinals.GetValueOrDefault(type);
                ordinals[type] = ordinal + 1;
                candidates.Add(new(type, intent, NumberText(e, type == "insulin" ? "dose" : "value") ?? NumberText(e, "value"),
                    String(e, "unit", 30), String(e, "product", 100), String(e, "date", 40),
                    String(e, "time", 40), String(e, "offset", 10), String(e, "time_evidence", 30) ?? "unknown",
                    ordinal, Long(e, "event_id")));
            }
            var unclear = new List<string>();
            if (root.TryGetProperty("unclear", out var u))
            {
                if (u.ValueKind != JsonValueKind.Array || u.GetArrayLength() > 20) return null;
                foreach (var item in u.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 500) return null;
                    unclear.Add(item.GetString()!);
                }
            }
            VetOperation? operation = null;
            if (root.TryGetProperty("operation", out var op) && op.ValueKind != JsonValueKind.Null)
            {
                if (!Closed(op, OperationFields)) return null;
                var kind = String(op, "kind", 30);
                if (kind is null || !Kinds.Contains(kind)) { unclear.Add("Нужна конкретная поддерживаемая операция."); }
                else
                {
                    var ids = new List<long>();
                    if (op.TryGetProperty("event_ids", out var array))
                    {
                        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 200) return null;
                        foreach (var id in array.EnumerateArray())
                            if (id.TryGetInt64(out var value) && value > 0) ids.Add(value); else return null;
                    }
                    var changes = new List<VetProfileChange>();
                    if (op.TryGetProperty("profile_changes", out var pc))
                    {
                        if (pc.ValueKind != JsonValueKind.Array || pc.GetArrayLength() > 7) return null;
                        foreach (var change in pc.EnumerateArray())
                        {
                            if (!Closed(change, ["field", "value"])) return null;
                            var field = String(change, "field", 100);
                            if (field is null) return null;
                            changes.Add(new(field, String(change, "value", 500)));
                        }
                    }
                    operation = new(kind, Long(op, "event_id"), ids, Long(op, "pending_id"),
                        Int(op, "review_revision"), changes);
                }
            }
            VetHistoryQuery? history = null;
            if (root.TryGetProperty("history_query", out var h) && h.ValueKind != JsonValueKind.Null)
            {
                if (!Closed(h, HistoryFields)) return null;
                history = new(String(h, "from_date", 20), String(h, "until_date", 20),
                    Int(h, "offset") ?? 0, h.TryGetProperty("analysis", out var a) && a.ValueKind == JsonValueKind.True);
            }
            VetPhotoOperation? photoOperation = null;
            if (root.TryGetProperty("photo_operation", out var photo) && photo.ValueKind != JsonValueKind.Null)
            {
                photoOperation = VetPhotoOperationParser.Parse(photo);
                if (photoOperation is null) return null;
            }
            VetPhotoCaptionInput? photoCaption = null;
            if (root.TryGetProperty("photo_caption", out var caption) && caption.ValueKind != JsonValueKind.Null)
            {
                photoCaption = VetPhotoCaptionInputParser.Parse(caption);
                if (photoCaption is null) return null;
            }
            return new(reply, candidates, unclear, operation, history)
            { PhotoOperation = photoOperation, PhotoCaption = photoCaption };
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static bool Closed(JsonElement e, HashSet<string> fields) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().All(p => fields.Contains(p.Name))
        && e.EnumerateObject().Select(p => p.Name).Distinct().Count() == e.EnumerateObject().Count();
    private static string? String(JsonElement e, string field, int max) =>
        !e.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= max ? value.GetString() :
        throw new JsonException();
    private static string? NumberText(JsonElement e, string field) =>
        !e.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.Number ? value.GetRawText() :
        value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 100 ? value.GetString() :
        throw new JsonException();
    private static long? Long(JsonElement e, string field) =>
        !e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null ? null :
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : throw new JsonException();
    private static int? Int(JsonElement e, string field) =>
        !e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null ? null :
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : throw new JsonException();

    public static bool TryPositiveDecimal(string? raw, out decimal value)
    {
        value = 0;
        if (raw is null || raw.Length > 100) return false;
        raw = raw.Replace(',', '.');
        var match = Regex.Match(raw, @"^\+?([0-9]+)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return false;
        var exponent = 0;
        if (match.Groups[3].Success && (!int.TryParse(match.Groups[3].Value, out exponent) || exponent is < -100 or > 100)) return false;
        var digits = match.Groups[1].Value + match.Groups[2].Value;
        var scale = match.Groups[2].Value.Length - exponent;
        if (scale < 0) { digits += new string('0', -scale); scale = 0; }
        if (scale >= digits.Length) digits = new string('0', scale - digits.Length + 1) + digits;
        var integer = scale == 0 ? digits : digits[..^scale];
        var fraction = scale == 0 ? "" : digits[^scale..].TrimEnd('0');
        if (fraction.Length > 28) return false;
        var normalized = integer.TrimStart('0');
        if (normalized.Length == 0) normalized = "0";
        if (fraction.Length != 0) normalized += "." + fraction;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) || value <= 0)
            return false;
        // Decimal.TryParse may round a large mantissa. Round-trip prevents any silent rounding.
        return value.ToString("0.############################", CultureInfo.InvariantCulture) == normalized;
    }
}
