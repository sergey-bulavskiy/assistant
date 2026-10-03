using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application.Health;

namespace Assistant.Evals;

/// <summary>One extraction eval case: one line of a cases file (an invented message, its expected
/// result and, optionally, a recorded model answer).</summary>
public sealed class EvalCase
{
    private const string NowFormat = "yyyy-MM-dd'T'HH:mm";

    private EvalCase(JsonObject json, string? sourceFile)
    {
        Json = json;
        SourceFile = sourceFile;
    }

    /// <summary>The whole line; recording replaces recorded_output in it.</summary>
    public JsonObject Json { get; }

    /// <summary>The file the case was read from (null for a case built in a test).</summary>
    public string? SourceFile { get; }

    public string Id => Json["id"]!.GetValue<string>();

    public string Text => Json["text"]!.GetValue<string>();

    public string TimeZone => Json["time_zone"]!.GetValue<string>();

    public bool Critical => Json["critical"]!.GetValue<bool>();

    public JsonObject Expected => Json["expected"]!.AsObject();

    /// <summary>The message's send time ("now", local time in TimeZone) as UTC.</summary>
    public DateTimeOffset SentAtUtc
    {
        get
        {
            var local = DateTime.ParseExact(Json["now"]!.GetValue<string>(), NowFormat, CultureInfo.InvariantCulture);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, ProfileTimeZone.Find(TimeZone)), TimeSpan.Zero);
        }
    }

    /// <summary>The recorded model answer as text: a JSON string as is, a JSON object as its JSON text.</summary>
    public string? RecordedOutput => Json["recorded_output"] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonNode node => node.ToJsonString()
    };

    /// <summary>Stores a model answer: as a JSON object when the whole answer is one, otherwise as a string.</summary>
    public void SetRecordedOutput(string modelText)
    {
        JsonNode? parsed = null;
        try
        {
            parsed = JsonNode.Parse(modelText.Trim());
        }
        catch (JsonException)
        {
            // Prose or a code fence around the JSON: kept as a string.
        }

        Json["recorded_output"] = parsed is JsonObject ? parsed : JsonValue.Create(modelText);
    }

    /// <summary>Reads one case line. Throws FormatException naming the first problem.</summary>
    public static EvalCase Parse(string line, string? sourceFile = null)
    {
        JsonObject json;
        try
        {
            json = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("a case must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"not valid JSON ({ex.GetType().Name})");
        }

        RequireString(json, "id");
        RequireString(json, "text");
        RequireString(json, "now");
        RequireString(json, "time_zone");
        if (!DateTime.TryParseExact(json["now"]!.GetValue<string>(), NowFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new FormatException("now must look like 2030-02-07T09:00");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(json["time_zone"]!.GetValue<string>(), out _))
        {
            throw new FormatException("time_zone is not a time zone id this runtime knows");
        }

        if (json["critical"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException("critical must be true or false");
        }

        if (json["expected"] is not JsonObject expected)
        {
            throw new FormatException("expected must be an object");
        }

        if (expected["events"] is not JsonArray events
            || events.Any(e => e is not JsonObject o || o["type"]?.GetValueKind() != JsonValueKind.String))
        {
            throw new FormatException("expected.events must be an array of objects, each with a type");
        }

        if (events.Any(e => e!["intent"] is { } intent
                && (intent.GetValueKind() != JsonValueKind.String || !ExtractionIntents.All.Contains(intent.GetValue<string>()))))
        {
            throw new FormatException("an expected event's intent must be record, question_only or unsure");
        }

        if (expected["unclear"] is not JsonArray unclear || unclear.Any(u => u?.GetValueKind() != JsonValueKind.String))
        {
            throw new FormatException("expected.unclear must be an array of reasons");
        }

        if (expected.ContainsKey("is_question")
            && expected["is_question"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException("expected.is_question must be true or false");
        }

        if (expected.ContainsKey("undo") && expected["undo"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException("expected.undo must be true or false");
        }

        if (expected["alert"] is { } alert
            && (alert is not JsonObject alertObject
                || alertObject["rule_key"]?.GetValueKind() != JsonValueKind.String
                || alertObject["level"]?.GetValueKind() != JsonValueKind.String))
        {
            throw new FormatException("expected.alert must be null or an object with rule_key and level");
        }

        if (json["critical"]!.GetValue<bool>() && !expected.ContainsKey("alert"))
        {
            throw new FormatException("a critical case must state expected.alert (null when no alert)");
        }

        return new EvalCase(json, sourceFile);
    }

    private static void RequireString(JsonObject json, string name)
    {
        if (json[name]?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(json[name]!.GetValue<string>()))
        {
            throw new FormatException($"{name} must be a non-empty string");
        }
    }
}
