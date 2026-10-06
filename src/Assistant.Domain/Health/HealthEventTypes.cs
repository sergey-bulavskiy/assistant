namespace Assistant.Domain.Health;

/// <summary>Values of events.type.</summary>
public static class HealthEventTypes
{
    public const string Glucose = "glucose";
    public const string Insulin = "insulin";
    public const string Meal = "meal";
    public const string Symptom = "symptom";
    public const string Weight = "weight";
    public const string BloodPressure = "blood_pressure";
    public const string Note = "note";

    public static IReadOnlyList<string> All { get; } = new[] { Glucose, Insulin, Meal, Symptom, Weight, BloodPressure, Note };
}
