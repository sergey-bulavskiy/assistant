using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class SafetyRuleDefaultsTests
{
    [Fact]
    public void All_contains_the_thirteen_rules_in_order()
    {
        SafetyRuleDefaults.All.Select(r => r.RuleKey).ToArray().ShouldBe(new[]
        {
            "glucose.any",
            "glucose.fasting",
            "glucose.after_1h",
            "glucose.after_2h",
            "blood_pressure.systolic",
            "blood_pressure.diastolic",
            "combo.bp_symptoms",
            "symptom.reduced_movement",
            "symptom.vision_disturbance",
            "symptom.epigastric_pain",
            "symptom.bleeding",
            "symptom.fluid_leak",
            "symptom.seizure"
        });
    }

    [Fact]
    public void Every_default_is_a_guideline_default()
    {
        SafetyRuleDefaults.All.ShouldAllBe(r => r.Source == "guideline_default");
    }

    [Theory]
    [InlineData("glucose.any", 3.0, 3.9, null, 11.0, 13.9, null, null)]
    [InlineData("glucose.fasting", null, null, 5.1, null, null, null, null)]
    [InlineData("glucose.after_1h", null, null, 7.0, null, null, null, null)]
    [InlineData("glucose.after_2h", null, null, 6.7, null, null, null, null)]
    [InlineData("blood_pressure.systolic", null, null, null, 140.0, 160.0, null, null)]
    [InlineData("blood_pressure.diastolic", null, null, null, 90.0, 110.0, null, null)]
    [InlineData("combo.bp_symptoms", null, null, null, null, null, null, 24)]
    [InlineData("symptom.reduced_movement", null, null, null, null, null, "urgent", null)]
    [InlineData("symptom.vision_disturbance", null, null, null, null, null, "alert", null)]
    [InlineData("symptom.epigastric_pain", null, null, null, null, null, "alert", null)]
    [InlineData("symptom.bleeding", null, null, null, null, null, "urgent", null)]
    [InlineData("symptom.fluid_leak", null, null, null, null, null, "urgent", null)]
    [InlineData("symptom.seizure", null, null, null, null, null, "urgent", null)]
    public void Values_match_the_published_defaults(
        string key, double? lowUrgent, double? lowAlert, double? targetHigh, double? highAlert, double? highUrgent,
        string? symptomLevel, int? windowHours)
    {
        var rule = SafetyRuleDefaults.All.Single(r => r.RuleKey == key);

        rule.LowUrgent.ShouldBe(ToDecimal(lowUrgent));
        rule.LowAlert.ShouldBe(ToDecimal(lowAlert));
        rule.TargetHigh.ShouldBe(ToDecimal(targetHigh));
        rule.HighAlert.ShouldBe(ToDecimal(highAlert));
        rule.HighUrgent.ShouldBe(ToDecimal(highUrgent));
        rule.SymptomLevel.ShouldBe(symptomLevel);
        rule.WindowHours.ShouldBe(windowHours);
    }

    [Fact]
    public void Find_ignores_case_and_returns_null_for_unknown_keys()
    {
        SafetyRuleDefaults.Find("GLUCOSE.ANY")!.RuleKey.ShouldBe("glucose.any");
        SafetyRuleDefaults.Find("glucose.unknown").ShouldBeNull();
    }

    private static decimal? ToDecimal(double? value) => value is null ? null : (decimal)value.Value;
}
