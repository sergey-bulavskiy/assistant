namespace Assistant.Application.Health;

/// <summary>glucose.context values. Unknown or missing → Other.</summary>
public static class GlucoseContexts
{
    public const string Fasting = "fasting";
    public const string BeforeMeal = "before_meal";
    public const string AfterMeal1h = "after_meal_1h";
    public const string AfterMeal2h = "after_meal_2h";
    public const string Bedtime = "bedtime";
    public const string Night = "night";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = new[] { Fasting, BeforeMeal, AfterMeal1h, AfterMeal2h, Bedtime, Night, Other };
}

/// <summary>Glucose units the model may report. Only mmol/L is recorded (no conversion).</summary>
public static class GlucoseUnits
{
    public const string MmolPerLiter = "mmol/L";
    public const string MgPerDeciliter = "mg/dL";
}

/// <summary>insulin.kind values. Unknown or missing → Unknown.</summary>
public static class InsulinKinds
{
    public const string Long = "long";
    public const string Short = "short";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = new[] { Long, Short, Unknown };
}

/// <summary>meal.meal_kind values. Unknown or missing → Other.</summary>
public static class MealKinds
{
    public const string Breakfast = "breakfast";
    public const string Lunch = "lunch";
    public const string Dinner = "dinner";
    public const string Snack = "snack";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = new[] { Breakfast, Lunch, Dinner, Snack, Other };
}

/// <summary>symptom.code values (fixed, neutral). Unknown or missing → Other.</summary>
public static class SymptomCodes
{
    public const string Headache = "headache";
    public const string VisionDisturbance = "vision_disturbance";
    public const string EpigastricPain = "epigastric_pain";
    public const string NauseaVomiting = "nausea_vomiting";
    public const string Swelling = "swelling";
    public const string Bleeding = "bleeding";
    public const string AbdominalPain = "abdominal_pain";
    public const string ShortnessOfBreath = "shortness_of_breath";
    public const string Seizure = "seizure";
    public const string Dizziness = "dizziness";
    public const string HypoSymptoms = "hypo_symptoms";
    public const string Fever = "fever";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Headache, VisionDisturbance, EpigastricPain, NauseaVomiting, Swelling, Bleeding,
        AbdominalPain, ShortnessOfBreath, Seizure, Dizziness, HypoSymptoms, Fever, Other
    };
}

/// <summary>Why something could not be recorded (extraction "unclear" items and validation failures).</summary>
public static class UnclearReasons
{
    public const string Unit = "unit";
    public const string Value = "value";
    public const string Time = "time";
    public const string Type = "type";

    public static IReadOnlyList<string> All { get; } = new[] { Unit, Value, Time, Type };
}
