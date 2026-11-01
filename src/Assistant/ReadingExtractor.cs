using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Assistant;

public sealed record Reading(decimal Value, string Unit);

public sealed class ReadingExtractor(HttpClient http, string apiKey, string model)
{
    public async Task<Reading?> ExtractAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            store = false,
            max_output_tokens = 256,
            reasoning = new { effort = "minimal" },
            instructions = "Extract the explicit numeric sensor reading and unit from the input. " +
                           "Return null for both fields if either is absent or unclear. " +
                           "Do not infer, calculate, diagnose, or invent a value or unit.",
            input = text,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "reading",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            value = new { type = new[] { "number", "null" } },
                            unit = new { type = new[] { "string", "null" } }
                        },
                        required = new[] { "value", "unit" },
                        additionalProperties = false
                    }
                }
            }
        });
        Console.WriteLine("extraction_requested");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("OpenAI request failed.");
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = body.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed" ||
                !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "message" ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var partType) || partType.GetString() != "output_text" ||
                        !part.TryGetProperty("text", out var valueText) || valueText.ValueKind != JsonValueKind.String)
                        continue;
                    using var reading = JsonDocument.Parse(valueText.GetString()!);
                    var value = reading.RootElement.GetProperty("value");
                    var unit = reading.RootElement.GetProperty("unit");
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) ||
                        number <= 0 || unit.ValueKind != JsonValueKind.String)
                        return null;
                    var unitText = unit.GetString();
                    return unitText is "mmol/L" or "mg/dL" ? new Reading(number, unitText) : null;
                }
            }
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
        return null;
    }
}
