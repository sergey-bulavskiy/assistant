using System.Text.RegularExpressions;
using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class SafetyAlertTextTests
{
    private const string U = "\U0001F6A8";
    private const string W = "⚠️";
    private const string Phone = "103 или 112";
    private const string Default = "guideline_default";
    private const string Doctor = "doctor";

    private static SafetyDecision UrgentLowGlucose() =>
        new("glucose.any", "urgent", SafetyAlertKind.Glucose, Default, Threshold: 3.0m, Value: 2.5m, IsLow: true);

    [Fact]
    public void Urgent_low_glucose() =>
        SafetyAlertText.Format(UrgentLowGlucose(), Phone).ShouldBe(
            U + " Глюкоза: 2.5. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). Порог 3.0 — не подтверждено врачом. Действуйте по плану врача.");

    [Fact]
    public void Low_glucose_alert() =>
        SafetyAlertText.Format(
            new SafetyDecision("glucose.any", "alert", SafetyAlertKind.Glucose, Default, Threshold: 3.9m, Value: 3.5m, IsLow: true),
            Phone).ShouldBe(
            W + " Глюкоза: 3.5 — ниже порога 3.9 (не подтверждено врачом). Свяжитесь с врачом. Если самочувствие ухудшается — вызовите скорую (103 или 112). Действуйте по плану врача.");

    [Fact]
    public void High_glucose_alert() =>
        SafetyAlertText.Format(
            new SafetyDecision("glucose.any", "alert", SafetyAlertKind.Glucose, Default, Threshold: 11.0m, Value: 12.0m, IsLow: false),
            Phone).ShouldBe(
            W + " Глюкоза: 12.0 — выше порога 11.0 (не подтверждено врачом). Свяжитесь с врачом. Если самочувствие ухудшается — вызовите скорую (103 или 112).");

    [Fact]
    public void Urgent_high_glucose_from_the_doctor() =>
        SafetyAlertText.Format(
            new SafetyDecision("glucose.any", "urgent", SafetyAlertKind.Glucose, Doctor, Threshold: 13.9m, Value: 14.2m, IsLow: false),
            Phone).ShouldBe(
            U + " Глюкоза: 14.2. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). Порог 13.9 — порог от врача.");

    [Fact]
    public void Doctor_decimals_are_shown() =>
        SafetyAlertText.Format(
            new SafetyDecision("glucose.any", "alert", SafetyAlertKind.Glucose, Doctor, Threshold: 4.25m, Value: 3.95m, IsLow: true),
            Phone).ShouldBe(
            W + " Глюкоза: 3.95 — ниже порога 4.25 (порог от врача). Свяжитесь с врачом. Если самочувствие ухудшается — вызовите скорую (103 или 112). Действуйте по плану врача.");

    [Fact]
    public void Systolic_alert() =>
        SafetyAlertText.Format(
            new SafetyDecision("blood_pressure.systolic", "alert", SafetyAlertKind.Systolic, Default, Threshold: 140m, Value: 150m),
            Phone).ShouldBe(
            W + " Верхнее давление: 150 — выше порога 140 (не подтверждено врачом). Свяжитесь с врачом. Если самочувствие ухудшается — вызовите скорую (103 или 112).");

    [Fact]
    public void Diastolic_urgent() =>
        SafetyAlertText.Format(
            new SafetyDecision("blood_pressure.diastolic", "urgent", SafetyAlertKind.Diastolic, Default, Threshold: 110m, Value: 112m),
            Phone).ShouldBe(
            U + " Нижнее давление: 112. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). Порог 110 — не подтверждено врачом.");

    [Fact]
    public void Urgent_symptom() =>
        SafetyAlertText.Format(
            new SafetyDecision("symptom.bleeding", "urgent", SafetyAlertKind.Symptom, Default, SymptomCode: "bleeding"),
            Phone).ShouldBe(
            U + " Кровотечение: это повод срочно обратиться к врачу или вызвать скорую (103 или 112). (не подтверждено врачом)");

    [Fact]
    public void Symptom_alert()
    {
        var decision = new SafetyDecision(
            "symptom.vision_disturbance", "alert", SafetyAlertKind.Symptom, Default, SymptomCode: "vision_disturbance");

        SafetyAlertText.Format(decision, Phone).ShouldBe(
            W + " Нарушение зрения: сообщите врачу сегодня. Если становится хуже — вызовите скорую (103 или 112). (не подтверждено врачом)");
        SafetyAlertText.Format(decision with { ThresholdSource = Doctor }, Phone).ShouldEndWith("(порог от врача)");
    }

    [Fact]
    public void Combination()
    {
        var decision = new SafetyDecision(
            "combo.bp_symptoms", "urgent", SafetyAlertKind.Combo, Default,
            Systolic: 150, Diastolic: 95, SymptomCode: "headache");

        SafetyAlertText.Format(decision, Phone).ShouldBe(
            U + " Давление 150/95 вместе с симптомом «головная боль». Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). (не подтверждено врачом)");
        SafetyAlertText.Format(decision with { ThresholdSource = Doctor }, Phone).ShouldEndWith("(порог от врача)");
    }

    [Fact]
    public void The_profiles_phone_is_used()
    {
        var text = SafetyAlertText.Format(UrgentLowGlucose(), "112");

        text.ShouldContain("вызовите скорую (112)");
        text.ShouldNotContain("103");
    }

    [Fact]
    public void Not_recorded_adds_the_fixed_sentence_on_a_new_line()
    {
        var decision = UrgentLowGlucose();

        SafetyAlertText.FormatNotRecorded(decision, Phone).ShouldBe(
            SafetyAlertText.Format(decision, Phone) + "\nНичего не записано — повторите сообщение позже.");
    }

    [Theory]
    [InlineData("doctor", "порог от врача")]
    [InlineData("guideline_default", "не подтверждено врачом")]
    [InlineData("Doctor", "не подтверждено врачом")]
    [InlineData("", "не подтверждено врачом")]
    public void Only_the_doctor_source_gets_the_doctor_label(string source, string expected) =>
        SafetyAlertText.SourceLabel(source).ShouldBe(expected);

    [Fact]
    public void Texts_never_advise_medicine_or_doses()
    {
        string[] banned = ["доз", "инсулин", "лекарств", "таблет", "единиц", "ед.", "мг", "увелич", "уменьш", "снизь", "примите"];

        foreach (var source in new[] { Default, Doctor })
        {
            var decisions = new[]
            {
                UrgentLowGlucose() with { ThresholdSource = source },
                new SafetyDecision("glucose.any", "alert", SafetyAlertKind.Glucose, source, Threshold: 3.9m, Value: 3.5m, IsLow: true),
                new SafetyDecision("glucose.any", "alert", SafetyAlertKind.Glucose, source, Threshold: 11.0m, Value: 12.0m),
                new SafetyDecision("glucose.any", "urgent", SafetyAlertKind.Glucose, source, Threshold: 13.9m, Value: 14.2m),
                new SafetyDecision("blood_pressure.systolic", "alert", SafetyAlertKind.Systolic, source, Threshold: 140m, Value: 150m),
                new SafetyDecision("blood_pressure.diastolic", "urgent", SafetyAlertKind.Diastolic, source, Threshold: 110m, Value: 112m),
                new SafetyDecision("symptom.bleeding", "urgent", SafetyAlertKind.Symptom, source, SymptomCode: "bleeding"),
                new SafetyDecision("symptom.vision_disturbance", "alert", SafetyAlertKind.Symptom, source, SymptomCode: "vision_disturbance"),
                new SafetyDecision("combo.bp_symptoms", "urgent", SafetyAlertKind.Combo, source, Systolic: 150, Diastolic: 95, SymptomCode: "headache"),
                new SafetyDecision("symptom.seizure", "urgent", SafetyAlertKind.Symptom, source, SymptomCode: "seizure")
            };

            foreach (var decision in decisions)
            {
                foreach (var text in new[] { SafetyAlertText.Format(decision, Phone), SafetyAlertText.FormatNotRecorded(decision, Phone) })
                {
                    text.ShouldContain("103 или 112");
                    (text.Contains("порог от врача") || text.Contains("не подтверждено врачом")).ShouldBeTrue();
                    foreach (var word in banned)
                    {
                        text.ToLowerInvariant().ShouldNotContain(word);
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_symptom_code_has_a_russian_label()
    {
        foreach (var code in SymptomCodes.All)
        {
            var label = SafetyAlertText.SymptomLabel(code);
            label.ShouldNotBeEmpty();
            Regex.IsMatch(label, "[A-Za-z]").ShouldBeFalse();
        }

        SafetyAlertText.SymptomLabel("unknown_code").ShouldBe("симптом");
        SafetyAlertText.SymptomLabel(null).ShouldBe("симптом");
    }
}
