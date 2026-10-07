namespace Assistant.Application.Vet;

public static class VetProfileValidation
{
    public static bool IsValid(string field, string? value) => field switch
    {
        "Name" or "InsulinProduct" => value is null || value.Length is > 0 and <= 100,
        "OwnerContextNote" or "ReportedVetGuidance" => value is null || value.Length is > 0 and <= 500,
        "GlucoseUnit" => value is null or "mmol/L",
        "InsulinUnit" => value is null or "U",
        "TimeZone" => value is null || IsIanaZone(value),
        _ => false
    };

    public static bool IsIanaZone(string value)
    {
        if (value.Length > 100 || value.Contains(' ')) return false;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            return zone.HasIanaId || value == "UTC";
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
