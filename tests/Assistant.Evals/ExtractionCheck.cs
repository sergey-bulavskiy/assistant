using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application.Health;
using Assistant.Domain.Health;

namespace Assistant.Evals;

/// <summary>Pass/fail of one case (no scores). Problems is empty when the case passed.</summary>
public sealed record CaseResult(string CaseId, bool Critical, IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}

/// <summary>Runs a model answer through the production steps of HealthAssistant (ExtractionParser,
/// HealthEventValidator, and SafetyRuleEvaluator with the published-guideline default rules, no
/// earlier events, now = the message time) and compares the outcome with the case's expected result.
/// Every valid event counts, whatever its intent (the rules check them all); an expected event's
/// "intent" and the expected "undo" are compared only when the case states them.</summary>
public static class ExtractionCheck
{
    private const string LocalTimeFormat = "yyyy-MM-dd HH:mm";

    public static CaseResult Run(EvalCase evalCase, string? modelText)
    {
        var problems = new List<string>();
        var output = ExtractionParser.Parse(modelText);
        if (output is null)
        {
            problems.Add("the answer is not a readable extraction JSON object");
            return new CaseResult(evalCase.Id, evalCase.Critical, problems);
        }

        var sentAt = evalCase.SentAtUtc;
        var valid = new List<(NewHealthEvent Event, string Intent)>();
        var unclear = output.Unclear.Select(u => Reason(u.Reason)).ToList();
        foreach (var extracted in output.Events)
        {
            var validation = HealthEventValidator.Validate(extracted, sentAt, evalCase.TimeZone);
            if (validation.Event is { } recordable)
            {
                valid.Add((recordable, extracted.Intent ?? ExtractionIntents.Record));
            }
            else if (validation.Problem is { } problem)
            {
                unclear.Add(Reason(problem.Reason));
            }
        }

        CompareEvents(evalCase, valid, problems);
        CompareUnclear(evalCase, unclear, problems);
        if (evalCase.Expected["is_question"] is { } question && question.GetValue<bool>() != output.IsQuestion)
        {
            problems.Add($"is_question: expected {question.GetValue<bool>()}, got {output.IsQuestion}");
        }

        if (evalCase.Expected["undo"] is { } undo && undo.GetValue<bool>() != output.Undo)
        {
            problems.Add($"undo: expected {undo.GetValue<bool>()}, got {output.Undo}");
        }

        if (evalCase.Expected.ContainsKey("alert"))
        {
            CompareAlert(evalCase, valid.Select(v => v.Event).ToList(), sentAt, problems);
        }

        return new CaseResult(evalCase.Id, evalCase.Critical, problems);
    }

    private static void CompareEvents(EvalCase evalCase, List<(NewHealthEvent Event, string Intent)> valid, List<string> problems)
    {
        var remaining = new List<(NewHealthEvent Event, string Intent)>(valid);
        foreach (var node in evalCase.Expected["events"]!.AsArray())
        {
            var want = node!.AsObject();
            var index = remaining.FindIndex(e => Matches(want, e.Event, e.Intent, evalCase.TimeZone));
            if (index < 0)
            {
                problems.Add("missing event " + want.ToJsonString());
            }
            else
            {
                remaining.RemoveAt(index);
            }
        }

        foreach (var (extra, intent) in remaining)
        {
            problems.Add(
                $"unexpected event {extra.Type} {extra.PayloadJson} at {LocalTime(extra, evalCase.TimeZone)} ({extra.OccurredAtSource}, {intent})");
        }
    }

    private static bool Matches(JsonObject want, NewHealthEvent actual, string intent, string timeZone)
    {
        if (want["type"]!.GetValue<string>() != actual.Type)
        {
            return false;
        }

        if (want["intent"] is { } wantIntent && wantIntent.GetValue<string>() != intent)
        {
            return false;
        }

        if (want["at"] is { } at)
        {
            if (actual.OccurredAtSource != OccurredAtSources.Stated || LocalTime(actual, timeZone) != at.GetValue<string>())
            {
                return false;
            }
        }
        else if (actual.OccurredAtSource != OccurredAtSources.Message)
        {
            return false;
        }

        using var payload = JsonDocument.Parse(actual.PayloadJson);
        foreach (var (name, value) in want)
        {
            if (name is "type" or "at" or "intent")
            {
                continue;
            }

            if (!payload.RootElement.TryGetProperty(name, out var got) || !SameValue(value, got))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameValue(JsonNode? want, JsonElement got) => want switch
    {
        null => got.ValueKind == JsonValueKind.Null,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number =>
            got.ValueKind == JsonValueKind.Number && got.GetDecimal() == value.GetValue<decimal>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.String =>
            got.ValueKind == JsonValueKind.String && got.GetString() == value.GetValue<string>(),
        _ => false
    };

    private static void CompareUnclear(EvalCase evalCase, List<string> unclear, List<string> problems)
    {
        var want = evalCase.Expected["unclear"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        var got = unclear.Order(StringComparer.Ordinal).ToList();
        if (!want.SequenceEqual(got))
        {
            problems.Add($"unclear: expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
        }
    }

    private static void CompareAlert(EvalCase evalCase, List<NewHealthEvent> valid, DateTimeOffset sentAt, List<string> problems)
    {
        var evaluations = SafetyRuleEvaluator.Evaluate(valid, Array.Empty<HealthEventInfo>(), SafetyRuleDefaults.All, sentAt);
        var decision = SafetyRuleEvaluator.MostSevere(evaluations.Select(e => e.Alert));
        var want = evalCase.Expected["alert"] as JsonObject;
        var wantText = want is null ? "none" : $"{want["rule_key"]!.GetValue<string>()} {want["level"]!.GetValue<string>()}";
        var gotText = decision is null ? "none" : $"{decision.RuleKey} {decision.Level}";
        if (wantText != gotText)
        {
            problems.Add($"alert: expected {wantText}, got {gotText}");
        }
    }

    // As the clarification text: a reason outside the known list counts as "value".
    private static string Reason(string? reason)
    {
        var normalized = reason?.Trim().ToLowerInvariant();
        return normalized is not null && UnclearReasons.All.Contains(normalized) ? normalized : UnclearReasons.Value;
    }

    private static string LocalTime(NewHealthEvent e, string timeZone) =>
        TimeZoneInfo.ConvertTime(e.OccurredAt, ProfileTimeZone.Find(timeZone)).ToString(LocalTimeFormat, CultureInfo.InvariantCulture);
}
