using System.Text.Json;
using Assistant.Application.Diagnostics;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceRedactor
{
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "source", "decision", "model_request", "model_result", "extraction", "confirmation",
        "answer", "delivery", "interaction"
    };

    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal)
    {
        "admitted", "skipped", "attempted", "ok", "failed", "unknown", "generated",
        "not_sent", "sent", "requested", "accepted", "declined", "expired", "cancelled",
        "completed", "replaced", "ignored"
    };

    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "already_processed", "duplicate", "offset_only", "service_or_non_text", "edit_suppressed",
        "not_addressed", "other_bot_command", "unknown_command", "bot_authored", "not_configured",
        "rate_limited", "daily_cap", "budget_exhausted", "all_models_unavailable",
        "provider_limit", "provider_timeout", "provider_failure", "empty_response",
        "extraction_failed", "invalid_extraction", "question_false", "answer_suppressed",
        "alert_precedence", "dose_advice_replaced", "pending_record", "delivery_failure",
        "delivery_timeout", "no_extraction_candidate", "fixed_alert", "events_recorded",
        "clarification", "no_events", "undo", "normal", "needs_reply_false", "not_eligible",
        "edited_message", "context_budget_exceeded"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TraceSecretRegistry _secrets;

    public TraceRedactor(TraceSecretRegistry secrets) => _secrets = secrets;

    public (string Value, bool Redacted, bool Truncated) SanitizeMetadata(string value, int maxChars)
    {
        var cleaned = _secrets.Redact(value);
        var shortened = Bound(cleaned, maxChars);
        return (shortened, !string.Equals(cleaned, value, StringComparison.Ordinal),
            shortened.Length != cleaned.Length);
    }

    public PreparedTraceEvent Prepare(TraceEventData data, int availableBytes)
    {
        Validate(data);

        var redacted = data.Text is not null && _secrets.Redact(data.Text) != data.Text
            || (data.Messages?.Any(m => _secrets.Redact(m.Text) != m.Text
                || m.Author is not null && _secrets.Redact(m.Author) != m.Author) ?? false)
            || data.Options is { } originalOptions && (
                _secrets.Redact(originalOptions.Model) != originalOptions.Model
                || originalOptions.ResponseFormat is not null
                    && _secrets.Redact(originalOptions.ResponseFormat) != originalOptions.ResponseFormat
                || originalOptions.Provider is not null
                    && _secrets.Redact(originalOptions.Provider) != originalOptions.Provider
                || originalOptions.Tier is not null
                    && _secrets.Redact(originalOptions.Tier) != originalOptions.Tier);

        var messages = data.Messages?.Take(64).Select(m => new TraceModelMessage(
            m.Role, Bound(_secrets.Redact(m.Text), TraceOptions.MaximumDetailBytes),
            m.Author is null ? null : Bound(_secrets.Redact(m.Author), 256))).ToArray();
        var options = data.Options is null ? null : new TraceModelOptions(
            Bound(_secrets.Redact(data.Options.Model), 128), data.Options.Temperature,
            data.Options.MaxOutputTokens,
            data.Options.ResponseFormat is null ? null : Bound(_secrets.Redact(data.Options.ResponseFormat), 64),
            data.Options.MaxOutputChars, data.Options.MaxOutputBytes, data.Options.TimeoutSeconds,
            data.Options.Provider is null ? null : Bound(_secrets.Redact(data.Options.Provider), 64),
            data.Options.Tier is null ? null : Bound(_secrets.Redact(data.Options.Tier), 32));
        var detail = new Detail(
            data.Text is null ? null : Bound(_secrets.Redact(data.Text), TraceOptions.MaximumDetailBytes),
            data.Sent, messages, options, data.PartIndex, data.PartCount, data.TelegramMessageId,
            data.EventCount, data.ProblemCount, data.Operation,
            data.Messages is null ? null : Math.Max(0, data.Messages.Count - 64),
            false, redacted);
        var truncated = data.Messages is { Count: > 64 }
            || (data.Text?.Length ?? 0) > TraceOptions.MaximumDetailBytes
            || (data.Messages?.Any(m => m.Text.Length > TraceOptions.MaximumDetailBytes || m.Author?.Length > 256) ?? false)
            || data.Options is { } rawOptions && (rawOptions.Model.Length > 128
                || rawOptions.ResponseFormat?.Length > 64 || rawOptions.Provider?.Length > 64
                || rawOptions.Tier?.Length > 32);
        var bytes = Encode(detail);
        if (bytes.Length <= availableBytes && !truncated)
            return new PreparedTraceEvent(data, JsonSerializer.Serialize(detail, JsonOptions), bytes.Length, false, redacted);

        // Keep a valid JSON object and trim its allowlisted strings at Unicode scalar boundaries.
        detail = detail with { Truncated = true };
        bytes = Encode(detail);
        while (bytes.Length > availableBytes)
        {
            if (detail.Text is { Length: > 0 } text)
            {
                var prefix = LongestFittingPrefix(text, candidate =>
                    Encode(detail with { Text = candidate }).Length <= availableBytes);
                detail = detail with { Text = prefix };
            }
            else if (detail.Messages is { Length: > 0 } remaining)
            {
                var copy = remaining.ToArray();
                var index = Array.FindLastIndex(copy, m => m.Text.Length > 0);
                if (index >= 0)
                {
                    var original = copy[index];
                    var prefix = LongestFittingPrefix(original.Text, candidate =>
                    {
                        copy[index] = original with { Text = candidate };
                        return Encode(detail with { Messages = copy }).Length <= availableBytes;
                    });
                    copy[index] = original with { Text = prefix };
                    detail = detail with { Messages = copy };
                }
                else detail = detail with { Messages = copy[..^1], OmittedMessages = (detail.OmittedMessages ?? 0) + 1 };
            }
            else if (detail.Options is not null) detail = detail with { Options = null };
            else if (detail.Text is not null) detail = detail with { Text = null };
            else if (detail.Messages is not null) detail = detail with { Messages = null };
            else break;
            bytes = Encode(detail);
        }

        if (bytes.Length > availableBytes)
            return new PreparedTraceEvent(data, null, 0, true, redacted);
        return new PreparedTraceEvent(data, JsonSerializer.Serialize(detail, JsonOptions), bytes.Length, true, redacted);
    }

    public static void Validate(TraceEventData data)
    {
        if (!Stages.Contains(data.Stage) || !Outcomes.Contains(data.Outcome)
            || (data.ReasonCode is not null && !Reasons.Contains(data.ReasonCode)))
            throw new ArgumentException("Trace event has an unknown diagnostic code.", nameof(data));
        if ((data.Messages is not null || data.Options is not null) && data.Stage != "model_request")
            throw new ArgumentException("Model input is allowed only on model_request.", nameof(data));
        if (data.Text is not null && data.Stage is not ("source" or "model_result" or "answer" or "delivery"))
            throw new ArgumentException("Text is not allowed at this trace stage.", nameof(data));
        if ((data.EventCount is not null || data.ProblemCount is not null) && data.Stage != "extraction")
            throw new ArgumentException("Extraction counts are allowed only on extraction.", nameof(data));
        if (data.Operation is not null && (data.Stage != "delivery"
            || data.Operation is not ("send_text" or "send_text_with_buttons" or "edit_text")))
            throw new ArgumentException("Text operation is not allowlisted.", nameof(data));
        if (data.Messages?.Any(m => m.Role is not ("system" or "user" or "assistant")) == true)
            throw new ArgumentException("Model message role is not allowlisted.", nameof(data));
    }

    public string Serialize(PreparedTraceEvent prepared) =>
        prepared.DetailJson ?? "{}";

    public static string SerializeMetadataOnly(TraceEventData data, bool redacted) => JsonSerializer.Serialize(
        new MetadataOnlyDetail(data.Sent, data.PartIndex, data.PartCount,
            data.TelegramMessageId, data.Operation, true, redacted), JsonOptions);

    private static byte[] Encode(Detail detail) => JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);

    private static string Bound(string value, int maxChars) =>
        value.Length <= maxChars ? value : Prefix(value, maxChars);

    private static string Prefix(string value, int chars)
    {
        chars = Math.Clamp(chars, 0, value.Length);
        if (chars > 0 && chars < value.Length && char.IsHighSurrogate(value[chars - 1])) chars--;
        return value[..chars];
    }

    private static string LongestFittingPrefix(string value, Func<string, bool> fits)
    {
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (fits(Prefix(value, mid))) low = mid;
            else high = mid - 1;
        }
        return Prefix(value, low);
    }

    private sealed record Detail(
        string? Text, bool? Sent, TraceModelMessage[]? Messages, TraceModelOptions? Options,
        int? PartIndex, int? PartCount, int? TelegramMessageId,
        int? EventCount, int? ProblemCount, string? Operation,
        int? OmittedMessages, bool Truncated, bool Redacted);

    private sealed record MetadataOnlyDetail(bool? Sent, int? PartIndex, int? PartCount,
        int? TelegramMessageId, string? Operation, bool Truncated, bool Redacted);

    public sealed record PreparedTraceEvent(TraceEventData Data, string? DetailJson, int PayloadBytes,
        bool Truncated, bool Redacted);
}
