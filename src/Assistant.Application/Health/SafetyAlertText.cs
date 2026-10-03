using System.Globalization;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>The fixed safety alert texts (Russian). Only code fills them: values, thresholds, the
/// source label and the profile's emergency phone. Never model text, never a medicine, a dose or a
/// treatment: they send the family to the doctor, the doctor's plan or emergency services.</summary>
public static class SafetyAlertText
{
    public const string DoctorLabel = "порог от врача";
    public const string DefaultLabel = SafetyRuleText.DefaultLabel;
    public const string LowGlucosePlanSentence = "Действуйте по плану врача.";
    public const string NothingRecordedSentence = "Ничего не записано — повторите сообщение позже.";

    private const string UrgentMark = "\U0001F6A8";
    private const string AlertMark = "⚠️";

    /// <summary>"порог от врача" only for a doctor's rule; anything else is labelled as unconfirmed.</summary>
    public static string SourceLabel(string thresholdSource) =>
        thresholdSource == SafetyRuleSources.Doctor ? DoctorLabel : DefaultLabel;

    public static string Format(SafetyDecision decision, string emergencyPhone)
    {
        var label = SourceLabel(decision.ThresholdSource);
        var urgent = decision.Level == SafetyAlertLevels.Urgent;
        switch (decision.Kind)
        {
            case SafetyAlertKind.Symptom:
            {
                var symptom = Capitalize(SymptomLabel(decision.SymptomCode));
                return urgent
                    ? $"{UrgentMark} {symptom}: это повод срочно обратиться к врачу или вызвать скорую ({emergencyPhone}). ({label})"
                    : $"{AlertMark} {symptom}: сообщите врачу сегодня. Если становится хуже — вызовите скорую ({emergencyPhone}). ({label})";
            }

            case SafetyAlertKind.Combo:
                return $"{UrgentMark} Давление {decision.Systolic}/{decision.Diastolic} вместе с симптомом «{SymptomLabel(decision.SymptomCode)}». " +
                       $"Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую ({emergencyPhone}). ({label})";

            default:
            {
                var (what, format) = decision.Kind switch
                {
                    SafetyAlertKind.Glucose => ("Глюкоза", "0.0#"),
                    SafetyAlertKind.Systolic => ("Верхнее давление", "0.##"),
                    _ => ("Нижнее давление", "0.##")
                };
                var value = Number(decision.Value, format);
                var threshold = Number(decision.Threshold, format);
                var direction = decision.IsLow ? "ниже" : "выше";
                var text = urgent
                    ? $"{UrgentMark} {what}: {value}. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую ({emergencyPhone}). Порог {threshold} — {label}."
                    : $"{AlertMark} {what}: {value} — {direction} порога {threshold} ({label}). Свяжитесь с врачом. " +
                      $"Если самочувствие ухудшается — вызовите скорую ({emergencyPhone}).";
                return decision.Kind == SafetyAlertKind.Glucose && decision.IsLow ? $"{text} {LowGlucosePlanSentence}" : text;
            }
        }
    }

    /// <summary>The alert for a reading that was not recorded (quick scan, or the save failed).</summary>
    public static string FormatNotRecorded(SafetyDecision decision, string emergencyPhone) =>
        $"{Format(decision, emergencyPhone)}\n{NothingRecordedSentence}";

    /// <summary>Neutral Russian label of a symptom code; an unknown code reads "симптом".</summary>
    public static string SymptomLabel(string? code) => code switch
    {
        SymptomCodes.Headache => "головная боль",
        SymptomCodes.VisionDisturbance => "нарушение зрения",
        SymptomCodes.EpigastricPain => "боль в верхней части живота",
        SymptomCodes.NauseaVomiting => "тошнота или рвота",
        SymptomCodes.Swelling => "отёки",
        SymptomCodes.Bleeding => "кровотечение",
        SymptomCodes.AbdominalPain => "боль в животе",
        SymptomCodes.ShortnessOfBreath => "одышка",
        SymptomCodes.Seizure => "судороги",
        SymptomCodes.Dizziness => "головокружение",
        SymptomCodes.HypoSymptoms => "признаки низкого сахара",
        SymptomCodes.Fever => "повышенная температура",
        _ => "симптом"
    };

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Number(decimal? value, string format) =>
        (value ?? 0m).ToString(format, CultureInfo.InvariantCulture);
}
