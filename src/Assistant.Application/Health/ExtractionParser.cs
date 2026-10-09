using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Pure, strict parser of the extraction answer (roles/health/extract.md contract). Takes
/// the text from the first '{' to the last '}' (drops a code fence or prose around it). Numbers may
/// be JSON numbers or strings with a comma or dot. Unknown fields are dropped; an event of an
/// unknown type becomes an unclear item with reason "type". Every event's intent is resolved
/// (ExtractionIntents.Resolve); exact known candidate duplicates are removed; "undo" defaults to false. Anything else that does not fit returns null (invalid_output).</summary>
public static class ExtractionParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleDecimalConverter() }
    };

    public static ExtractionOutput? Parse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return null;
        }

        var start = modelText.IndexOf('{');
        var end = modelText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        OutputDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<OutputDto>(modelText[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null)
        {
            return null;
        }

        var isQuestion = dto.IsQuestion ?? false;
        var typed = (dto.Events ?? new List<ExtractedEvent?>())
            .OfType<ExtractedEvent>()
            .Select(e => e with { Type = e.Type?.Trim().ToLowerInvariant(), Intent = ExtractionIntents.Resolve(e.Intent, isQuestion) })
            .ToArray();
        var events = typed.Where(e => e.Type is not null && HealthEventTypes.All.Contains(e.Type))
            .Distinct(ExactCandidateComparer.Instance).ToArray();

        // An event of an unknown type is not silently lost: the person is asked about it.
        var unclear = (dto.Unclear ?? new List<ExtractedUnclear?>()).OfType<ExtractedUnclear>()
            .Concat(typed.Where(e => e.Type is null || !HealthEventTypes.All.Contains(e.Type))
                .Select(_ => new ExtractedUnclear { Fragment = null, Reason = UnclearReasons.Type }))
            .ToArray();
        return new ExtractionOutput(events, unclear, isQuestion, dto.Undo ?? false, dto.NeedsReply ?? false);
    }

    // Record equality covers every scalar field; tags need ordered structural equality.
    private sealed class ExactCandidateComparer : IEqualityComparer<ExtractedEvent>
    {
        public static readonly ExactCandidateComparer Instance = new();

        public bool Equals(ExtractedEvent? x, ExtractedEvent? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return (x with { Tags = null }) == (y with { Tags = null })
                && (x.Tags is null ? y.Tags is null : y.Tags is not null
                    && x.Tags.SequenceEqual(y.Tags, StringComparer.Ordinal));
        }

        public int GetHashCode(ExtractedEvent value)
        {
            var hash = new HashCode();
            hash.Add(value with { Tags = null });
            hash.Add(value.Tags is not null);
            if (value.Tags is not null)
                foreach (var tag in value.Tags) hash.Add(tag, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class OutputDto
    {
        public List<ExtractedEvent?>? Events { get; set; }
        public List<ExtractedUnclear?>? Unclear { get; set; }
        public bool? IsQuestion { get; set; }
        public bool? Undo { get; set; }
        public bool? NeedsReply { get; set; }
    }

    /// <summary>Accepts 7.8, "7.8" and "7,8"; a string that is not a number becomes null.</summary>
    private sealed class FlexibleDecimalConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    return reader.TryGetDecimal(out var number) ? number : throw new JsonException("Number out of range.");
                case JsonTokenType.String:
                    var text = reader.GetString()?.Trim().Replace(',', '.');
                    return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null;
                default:
                    throw new JsonException("Expected a number.");
            }
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Extraction output is only read.");
    }
}
