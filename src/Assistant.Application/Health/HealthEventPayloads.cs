using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Assistant.Application.Health;

/// <summary>Glucose in mmol/L (the only unit recorded).</summary>
public sealed record GlucosePayload(decimal Value, string Context);

public sealed record InsulinPayload(string Kind, string? Name, decimal Units);

public sealed record MealPayload(string MealKind, string Description);

public sealed record SymptomPayload(string Code, string Text);

public sealed record WeightPayload(decimal Kg);

public sealed record BloodPressurePayload(int Systolic, int Diastolic, int? Pulse);

public sealed record NotePayload(string Text, string[] Tags);

/// <summary>events.payload JSON: snake_case property names, Cyrillic kept readable.</summary>
public static class HealthEventPayloads
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);

    /// <summary>Null when the JSON is not a document of this shape.</summary>
    public static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when both texts are the same JSON document. Property order, whitespace and
    /// number formatting are ignored (Postgres reformats jsonb text). False when either is not JSON.</summary>
    public static bool SameJson(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
