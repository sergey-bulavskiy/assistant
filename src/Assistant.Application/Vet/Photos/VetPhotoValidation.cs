using System.Globalization;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoPreservedTime(DateTimeOffset OccurredAt, string LocalTime,
    string TimeZoneSnapshot, string TimeEvidence);
public sealed record VetPhotoContext(string? RawValue = null, string? Unit = null,
    int? Year = null, int? Month = null, int? Day = null, string? Time = null, string? Offset = null,
    bool YearBoundaryAmbiguous = false, bool CorrectionApproved = false, int? SelectedDisplayIndex = null)
{
    public VetPhotoPreservedTime? PreservedTime { get; init; }
}
public sealed record VetPhotoEffectiveReading(decimal Value, string Unit, DateTimeOffset OccurredAt,
    string LocalTime, string TimeZoneSnapshot, string ValueEvidence, string UnitEvidence, string TimeEvidence,
    bool UsesProfileDefaults);
public sealed record VetPhotoValidation(VetPhotoEffectiveReading? Effective, IReadOnlyList<string> Reasons);

public static class VetPhotoValidationRules
{
    public static string ValueUnitEvidence(VetPhotoEffectiveReading reading) =>
        reading.ValueEvidence == "human_correction" && reading.UnitEvidence == "human_correction"
            ? "human_correction" : reading.ValueEvidence + "/" + reading.UnitEvidence;

    public static VetPhotoValidation Validate(VetPhotoInterpretation image, VetPhotoContext context,
        VetPhotoBatchAssumptions assumptions, DateTimeOffset sourceReceivedAt)
    {
        VetPhotoValidation Pending(string reason) => new(null, [reason]);
        if (image.Kind != "meter") return Pending(image.Kind == "unsupported" ? "unsupported_display" : "unreadable_display");
        if (image.Displays.Count == 0) return Pending("missing_display");
        var selected = context.SelectedDisplayIndex;
        if (image.Displays.Count > 1 && (!context.CorrectionApproved || selected is null)) return Pending("multiple_displays");
        var index = selected ?? 0;
        if (index < 0 || index >= image.Displays.Count || selected is not null && !context.CorrectionApproved) return Pending("invalid_display_selection");
        var d = image.Displays[index];
        if (image.Reasons.Count != 0 && !context.CorrectionApproved) return Pending("uncertain_display");
        if (context.RawValue is { } captionValue && !context.CorrectionApproved
            && (!VetInterpretationParser.TryPositiveDecimal(captionValue, out var captionNumber)
                || d.NumericValue != captionNumber)) return Pending("caption_value_conflict");
        var raw = context.CorrectionApproved && context.RawValue is not null ? context.RawValue : d.ValueText;
        if (!VetInterpretationParser.TryPositiveDecimal(raw, out var value)
            || !context.CorrectionApproved && d.NumericValue is null) return Pending("invalid_or_unreadable_value");
        var imageUnit = NormalizeUnit(d.Unit);
        var captionUnit = NormalizeUnit(context.Unit);
        if (context.Unit is not null && d.Unit is not null && captionUnit != imageUnit && !context.CorrectionApproved)
            return Pending("caption_unit_conflict");
        var explicitUnit = context.CorrectionApproved && context.Unit is not null ? captionUnit : imageUnit ?? captionUnit;
        var unit = explicitUnit ?? NormalizeUnit(assumptions.GlucoseUnit ?? assumptions.ProfileGlucoseUnit);
        if (unit != "mmol/L") return Pending(unit is null ? "missing_unit" : "unsupported_unit");
        var usesDefaultUnit = explicitUnit is null;
        var unitEvidence = context.CorrectionApproved && context.Unit is not null ? "human_correction" : d.Unit is not null ? "image" : context.Unit is not null ? "caption" : "batch_default";
        if (!context.CorrectionApproved && (Different(d.Year, context.Year) || Different(d.Month, context.Month)
            || Different(d.Day, context.Day) || Different(d.Time, context.Time) || Different(d.Offset, context.Offset)))
            return Pending("caption_time_conflict");
        if (context.PreservedTime is { } preserved)
        {
            if (!context.CorrectionApproved || context.Year != null || context.Month != null || context.Day != null
                || context.Time != null || context.Offset != null
                || preserved.TimeEvidence is not ("image_or_caption" or "batch_year" or "human_correction")
                || preserved.TimeZoneSnapshot is not { Length: > 0 and <= 256 }
                || !DateTime.TryParseExact(preserved.LocalTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) || preserved.OccurredAt > sourceReceivedAt.AddMinutes(10))
                return Pending("invalid_preserved_time");
            return new(new(value, "mmol/L", preserved.OccurredAt, preserved.LocalTime, preserved.TimeZoneSnapshot,
                context.RawValue != null ? "human_correction" : "image", unitEvidence, preserved.TimeEvidence, usesDefaultUnit), []);
        }
        int? Choose(int? pixel, int? caption) => context.CorrectionApproved ? caption ?? pixel : pixel ?? caption;
        string? ChooseText(string? pixel, string? caption) => context.CorrectionApproved ? caption ?? pixel : pixel ?? caption;
        var year = Choose(d.Year, context.Year);
        var month = Choose(d.Month, context.Month);
        var day = Choose(d.Day, context.Day);
        if (year is null)
        {
            if (context.YearBoundaryAmbiguous) return Pending("year_boundary_ambiguous");
            if (assumptions.Year is null || !assumptions.YearConfirmed) return Pending("batch_year_confirmation_required");
            year = assumptions.Year;
        }
        if (month is null || day is null) return Pending("missing_measurement_date");
        DateTime local;
        var clock = ChooseText(d.Time, context.Time);
        if (clock is null || !TimeOnly.TryParseExact(clock, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return Pending("missing_or_invalid_measurement_time");
        try { local = new DateOnly(year.Value, month.Value, day.Value).ToDateTime(time, DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return Pending("invalid_measurement_date"); }
        var offsetText = ChooseText(d.Offset, context.Offset);
        DateTimeOffset utc;
        string snapshot;
        var usesDefaultZone = false;
        if (offsetText is not null)
        {
            if (!TryOffset(offsetText, out var offset)) return Pending("invalid_offset");
            try { utc = new DateTimeOffset(local, offset).ToUniversalTime(); }
            catch (ArgumentException) { return Pending("invalid_measurement_date"); }
            snapshot = offsetText;
        }
        else
        {
            var zoneId = assumptions.TimeZone ?? assumptions.ProfileTimeZone;
            if (zoneId is null) return Pending("missing_time_zone");
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (TimeZoneNotFoundException) { return Pending("invalid_time_zone"); }
            catch (InvalidTimeZoneException) { return Pending("invalid_time_zone"); }
            if (zone.IsInvalidTime(local)) return Pending("invalid_local_time");
            if (zone.IsAmbiguousTime(local)) return Pending("ambiguous_local_time");
            try { utc = new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero); }
            catch (ArgumentException) { return Pending("invalid_measurement_date"); }
            snapshot = zone.Id;
            usesDefaultZone = assumptions.TimeZone is null || !assumptions.TimeZoneConfirmed;
        }
        if (utc > sourceReceivedAt.AddMinutes(10)) return Pending("future_measurement_time");
        return new(new(value, "mmol/L", utc, local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), snapshot,
            context.CorrectionApproved && context.RawValue is not null ? "human_correction" : "image", unitEvidence,
            context.CorrectionApproved && (context.Year is not null || context.Month is not null || context.Day is not null || context.Time is not null || context.Offset is not null)
                ? "human_correction" : d.Year is null && context.Year is null ? "batch_year" : "image_or_caption",
            usesDefaultUnit || usesDefaultZone), []);
    }

    public static string? NormalizeUnit(string? unit)
    {
        if (unit is null) return null;
        var compact = unit.Replace(" ", "", StringComparison.Ordinal);
        return compact.Equals("mmol/L", StringComparison.OrdinalIgnoreCase) || compact.Equals("ммоль/л", StringComparison.OrdinalIgnoreCase)
            ? "mmol/L" : unit;
    }
    private static bool Different<T>(T? first, T? second) where T : struct => first is not null && second is not null && !first.Equals(second);
    private static bool Different(string? first, string? second) => first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal);
    private static bool TryOffset(string raw, out TimeSpan offset)
    {
        offset = default;
        if (raw.Length != 6 || raw[0] is not ('+' or '-') || raw[3] != ':'
            || !int.TryParse(raw.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(raw.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours > 14 || minutes > 59 || hours == 14 && minutes != 0) return false;
        offset = new(hours, minutes, 0);
        if (raw[0] == '-') offset = -offset;
        return true;
    }
}
