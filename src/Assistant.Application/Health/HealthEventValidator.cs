using System.Globalization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Either a recordable event or the reason it cannot be recorded.</summary>
public sealed record EventValidation(NewHealthEvent? Event, ExtractedUnclear? Problem);

/// <summary>Pure plausibility checks (spec §6.1: "could this be a real entry", not medical judgment)
/// and timestamp building. Code, not the model, turns the model's local day/time into UTC with the
/// profile's time zone.</summary>
public static class HealthEventValidator
{
    public const int MinDay = -30;
    public static readonly TimeSpan MaxAfterMessage = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxBeforeMessage = TimeSpan.FromDays(30);

    private const decimal MinGlucose = 0.5m;
    private const decimal MaxGlucose = 35.0m;
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;
    private const int MaxSymptomTextLength = 200;
    private static readonly string[] TimeFormats = { "HH:mm", "H:mm" };

    public static EventValidation Validate(ExtractedEvent extracted, DateTimeOffset messageSentAt, string timeZoneId)
    {
        var type = Normalize(extracted.Type);
        var check = type switch
        {
            HealthEventTypes.Glucose => Glucose(extracted),
            HealthEventTypes.Insulin => Insulin(extracted),
            HealthEventTypes.Meal => Meal(extracted),
            HealthEventTypes.Symptom => Symptom(extracted),
            HealthEventTypes.Weight => Weight(extracted),
            HealthEventTypes.BloodPressure => BloodPressure(extracted),
            HealthEventTypes.Note => Note(extracted),
            _ => Bad(null, UnclearReasons.Type)
        };
        if (check.Problem is not null || check.Json is null)
        {
            return new EventValidation(null, check.Problem ?? Unclear(null, UnclearReasons.Type));
        }

        var occurred = ResolveOccurredAt(extracted, messageSentAt, timeZoneId);
        if (occurred is null)
        {
            return new EventValidation(null, Unclear(extracted.Time, UnclearReasons.Time));
        }

        return new EventValidation(new NewHealthEvent(type, occurred.Value.At, occurred.Value.Source, check.Json), null);
    }

    private sealed record PayloadCheck(string? Json, ExtractedUnclear? Problem);

    private static PayloadCheck Ok(object payload) => new(HealthEventPayloads.Serialize(payload), null);

    private static PayloadCheck Bad(string? fragment, string reason) => new(null, Unclear(fragment, reason));

    private static ExtractedUnclear Unclear(string? fragment, string reason) => new() { Fragment = fragment, Reason = reason };

    private static PayloadCheck Note(ExtractedEvent e)
    {
        var text = e.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 500
            || !HealthNoteTags.TryNormalizeMany(e.Tags, out var tags))
        {
            return Bad(null, UnclearReasons.Value);
        }
        return Ok(new NotePayload(text, tags));
    }

    private static PayloadCheck Glucose(ExtractedEvent e)
    {
        if (e.Value is not { } value)
        {
            return Bad(null, UnclearReasons.Value);
        }

        var fragment = Number(value);
        var unit = e.Unit?.Trim();
        var hasUnit = !string.IsNullOrEmpty(unit);
        if (hasUnit && !string.Equals(unit, GlucoseUnits.MmolPerLiter, StringComparison.OrdinalIgnoreCase))
        {
            // mg/dL (no conversion) or a unit we do not know.
            return Bad(fragment, UnclearReasons.Unit);
        }

        if (value > MaxGlucose)
        {
            // A bare number above the plausible mmol/L range is most likely another unit.
            return Bad(fragment, hasUnit ? UnclearReasons.Value : UnclearReasons.Unit);
        }

        if (value < MinGlucose)
        {
            return Bad(fragment, UnclearReasons.Value);
        }

        return Ok(new GlucosePayload(value, OneOf(e.Context, GlucoseContexts.All, GlucoseContexts.Other)));
    }

    // Recorded only: no rule and no advice ever looks at insulin entries.
    private static PayloadCheck Insulin(ExtractedEvent e)
    {
        if (e.Units is not { } units)
        {
            return Bad(null, UnclearReasons.Value);
        }

        if (units < 0.5m || units > 100m || decimal.Remainder(units * 2, 1m) != 0)
        {
            return Bad(Number(units), UnclearReasons.Value);
        }

        var name = e.Name?.Trim();
        return Ok(new InsulinPayload(
            OneOf(e.Kind, InsulinKinds.All, InsulinKinds.Unknown),
            string.IsNullOrEmpty(name) ? null : Truncate(name, MaxNameLength),
            units));
    }

    private static PayloadCheck Meal(ExtractedEvent e)
    {
        var description = e.Description?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            return Bad(null, UnclearReasons.Value);
        }

        return Ok(new MealPayload(OneOf(e.MealKind, MealKinds.All, MealKinds.Other), Truncate(description, MaxDescriptionLength)));
    }

    private static PayloadCheck Symptom(ExtractedEvent e) =>
        Ok(new SymptomPayload(
            OneOf(e.Code, SymptomCodes.All, SymptomCodes.Other),
            Truncate(e.Text?.Trim() ?? string.Empty, MaxSymptomTextLength)));

    private static PayloadCheck Weight(ExtractedEvent e)
    {
        if (e.Kg is not { } kg)
        {
            return Bad(null, UnclearReasons.Value);
        }

        return kg is < 30m or > 250m ? Bad(Number(kg), UnclearReasons.Value) : Ok(new WeightPayload(kg));
    }

    private static PayloadCheck BloodPressure(ExtractedEvent e)
    {
        if (e.Systolic is not { } systolic || e.Diastolic is not { } diastolic)
        {
            return Bad(null, UnclearReasons.Value);
        }

        var fragment = $"{Number(systolic)}/{Number(diastolic)}";
        if (!IsWhole(systolic) || !IsWhole(diastolic)
            || systolic is < 60m or > 260m || diastolic is < 30m or > 160m || systolic <= diastolic)
        {
            return Bad(fragment, UnclearReasons.Value);
        }

        if (e.Pulse is { } pulse && (!IsWhole(pulse) || pulse is < 30m or > 220m))
        {
            return Bad(fragment, UnclearReasons.Value);
        }

        return Ok(new BloodPressurePayload((int)systolic, (int)diastolic, e.Pulse is { } p ? (int)p : null));
    }

    /// <summary>UTC time of the reading, or null when it cannot be placed (spec §6.1 window:
    /// not more than 10 minutes after the message and not more than 30 days before it).</summary>
    private static (DateTimeOffset At, string Source)? ResolveOccurredAt(ExtractedEvent e, DateTimeOffset messageSentAt, string timeZoneId)
    {
        var day = e.Day ?? 0;
        if (day is > 0 or < MinDay)
        {
            return null;
        }

        var sentUtc = messageSentAt.ToUniversalTime();
        var zone = ProfileTimeZone.Find(timeZoneId);
        var localSent = TimeZoneInfo.ConvertTime(sentUtc, zone).DateTime;

        DateTime local;
        if (string.IsNullOrWhiteSpace(e.Time))
        {
            if (day == 0)
            {
                return (sentUtc, OccurredAtSources.Message);
            }

            // Only the day was stated ("вчера"): that day at the message's local time of day.
            local = localSent.AddDays(day);
        }
        else
        {
            if (!TimeOnly.TryParseExact(e.Time.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return null;
            }

            local = DateOnly.FromDateTime(localSent).AddDays(day).ToDateTime(time);
        }

        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            return null; // A local time skipped or repeated by a clock change: ask.
        }

        var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        if (utc > sentUtc + MaxAfterMessage || utc < sentUtc - MaxBeforeMessage)
        {
            return null;
        }

        return (utc, OccurredAtSources.Stated);
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string OneOf(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        var normalized = Normalize(value);
        return allowed.Contains(normalized) ? normalized : fallback;
    }

    private static bool IsWhole(decimal value) => decimal.Truncate(value) == value;

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];
}
