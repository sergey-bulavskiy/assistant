using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class SafetyRuleTextTests
{
    [Fact]
    public void Formats_glucose_with_one_decimal()
    {
        SafetyRuleText.Format(SafetyRuleDefaults.Find("glucose.any")!)
            .ShouldBe("glucose.any: low_urgent 3.0, low_alert 3.9, high_alert 11.0, high_urgent 13.9 — не подтверждено врачом");
    }

    [Fact]
    public void Formats_blood_pressure_without_trailing_zeros()
    {
        SafetyRuleText.Format(SafetyRuleDefaults.Find("blood_pressure.systolic")!)
            .ShouldBe("blood_pressure.systolic: high_alert 140, high_urgent 160 — не подтверждено врачом");
    }

    [Fact]
    public void Formats_symptom_and_combo_rules()
    {
        SafetyRuleText.Format(SafetyRuleDefaults.Find("symptom.reduced_movement")!)
            .ShouldBe("symptom.reduced_movement: symptom_level urgent — не подтверждено врачом");
        SafetyRuleText.Format(SafetyRuleDefaults.Find("combo.bp_symptoms")!)
            .ShouldBe("combo.bp_symptoms: window_hours 24 — не подтверждено врачом");
    }

    [Fact]
    public void Doctor_values_show_the_doctor_label_and_two_decimals()
    {
        var rule = SafetyRuleDefaults.Find("glucose.fasting")! with { TargetHigh = 5.25m, Source = "doctor" };

        SafetyRuleText.Format(rule).ShouldBe("glucose.fasting: target_high 5.25 — врач");
    }

    [Fact]
    public void UsedBy_lists_only_the_rules_non_null_fields_in_display_order()
    {
        SafetyRuleFields.UsedBy(SafetyRuleDefaults.Find("glucose.any")!)
            .ShouldBe(new[] { "low_urgent", "low_alert", "high_alert", "high_urgent" });
        SafetyRuleFields.UsedBy(SafetyRuleDefaults.Find("glucose.fasting")!).ShouldBe(new[] { "target_high" });
        SafetyRuleFields.UsedBy(SafetyRuleDefaults.Find("symptom.bleeding")!).ShouldBe(new[] { "symptom_level" });
        SafetyRuleFields.UsedBy(SafetyRuleDefaults.Find("combo.bp_symptoms")!).ShouldBe(new[] { "window_hours" });
    }
}
