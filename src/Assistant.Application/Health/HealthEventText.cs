using System.Globalization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>How an event is shown in /today and delete replies, e.g.
/// "#12 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)". An unreadable payload shows the type.</summary>
public static class HealthEventText
{
    public static string Line(HealthEventInfo e, TimeZoneInfo zone) =>
        $"#{e.Id.ToString(CultureInfo.InvariantCulture)} {TimeZoneInfo.ConvertTime(e.OccurredAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture)} {Describe(e)}";

    /// <summary>A validated event that is not saved yet (e.g. waiting for Да/Нет).</summary>
    public static string Describe(NewHealthEvent e) => Describe(new HealthEventInfo(0, e.Type, e.OccurredAt, e.PayloadJson, null));

    public static string Describe(HealthEventInfo e) => e.Type switch
    {
        HealthEventTypes.Glucose => HealthEventPayloads.TryDeserialize<GlucosePayload>(e.PayloadJson) is { } g ? Glucose(g) : e.Type,
        HealthEventTypes.Insulin => HealthEventPayloads.TryDeserialize<InsulinPayload>(e.PayloadJson) is { } i ? Insulin(i) : e.Type,
        HealthEventTypes.Meal => HealthEventPayloads.TryDeserialize<MealPayload>(e.PayloadJson) is { } m ? Meal(m) : e.Type,
        HealthEventTypes.Symptom => HealthEventPayloads.TryDeserialize<SymptomPayload>(e.PayloadJson) is { } s ? Symptom(s) : e.Type,
        HealthEventTypes.Weight => HealthEventPayloads.TryDeserialize<WeightPayload>(e.PayloadJson) is { } w ? Weight(w) : e.Type,
        HealthEventTypes.BloodPressure => HealthEventPayloads.TryDeserialize<BloodPressurePayload>(e.PayloadJson) is { } b ? BloodPressure(b) : e.Type,
        _ => e.Type
    };

    private static string Glucose(GlucosePayload p)
    {
        var context = p.Context switch
        {
            GlucoseContexts.Fasting => "натощак",
            GlucoseContexts.BeforeMeal => "перед едой",
            GlucoseContexts.AfterMeal1h => "через 1 ч после еды",
            GlucoseContexts.AfterMeal2h => "через 2 ч после еды",
            GlucoseContexts.Bedtime => "перед сном",
            GlucoseContexts.Night => "ночью",
            _ => null
        };
        var value = p.Value.ToString("0.0#", CultureInfo.InvariantCulture);
        return context is null ? $"глюкоза {value} ммоль/л" : $"глюкоза {value} ммоль/л ({context})";
    }

    private static string Insulin(InsulinPayload p)
    {
        var text = $"инсулин {p.Units.ToString("0.#", CultureInfo.InvariantCulture)} ед.";
        var kind = p.Kind switch
        {
            InsulinKinds.Long => "длинный",
            InsulinKinds.Short => "короткий",
            _ => null
        };
        if (kind is not null)
        {
            text += $", {kind}";
        }

        if (!string.IsNullOrEmpty(p.Name))
        {
            text += $", {p.Name}";
        }

        return text;
    }

    private static string Meal(MealPayload p)
    {
        var kind = p.MealKind switch
        {
            MealKinds.Breakfast => "завтрак",
            MealKinds.Lunch => "обед",
            MealKinds.Dinner => "ужин",
            MealKinds.Snack => "перекус",
            _ => "еда"
        };
        return $"{kind}: {p.Description}";
    }

    private static string Symptom(SymptomPayload p) =>
        $"симптом: {(string.IsNullOrEmpty(p.Text) ? p.Code : p.Text)}";

    private static string Weight(WeightPayload p) =>
        $"вес {p.Kg.ToString("0.##", CultureInfo.InvariantCulture)} кг";

    private static string BloodPressure(BloodPressurePayload p) =>
        p.Pulse is { } pulse
            ? $"давление {p.Systolic}/{p.Diastolic}, пульс {pulse}"
            : $"давление {p.Systolic}/{p.Diastolic}";
}
