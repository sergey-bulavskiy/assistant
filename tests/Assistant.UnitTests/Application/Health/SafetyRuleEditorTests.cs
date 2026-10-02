using Assistant.Application.Health;
using Assistant.Domain.Health;

namespace Assistant.UnitTests.Application.Health;

public class SafetyRuleEditorTests
{
    private const string ValueError = "Значение: число больше 0 и меньше 1000, не больше двух знаков после запятой.";
    private const string OrderError = "Не сохранено: нужно low_urgent ≤ low_alert ≤ target_high ≤ high_alert ≤ high_urgent.";

    private static SafetyRuleInfo Rule(string key) => SafetyRuleDefaults.Find(key)!;

    [Fact]
    public void Sets_a_numeric_field_with_a_comma_and_marks_it_doctor()
    {
        var edit = SafetyRuleEditor.SetField(Rule("glucose.any"), "low_alert", "4,0");

        edit.Error.ShouldBeNull();
        var rule = edit.Rule.ShouldNotBeNull();
        rule.LowAlert.ShouldBe(4.0m);
        rule.Source.ShouldBe(SafetyRuleSources.Doctor);
        rule.LowUrgent.ShouldBe(3.0m);
        rule.HighAlert.ShouldBe(11.0m);
        rule.HighUrgent.ShouldBe(13.9m);
    }

    [Fact]
    public void Field_names_ignore_case()
    {
        var edit = SafetyRuleEditor.SetField(Rule("glucose.any"), "LOW_ALERT", "4.0");

        edit.Rule.ShouldNotBeNull().LowAlert.ShouldBe(4.0m);
    }

    [Fact]
    public void Only_fields_the_rule_uses_can_change()
    {
        var unused = SafetyRuleEditor.SetField(Rule("glucose.fasting"), "high_alert", "9");
        unused.Rule.ShouldBeNull();
        unused.Error.ShouldBe("У правила glucose.fasting нет поля high_alert. Поля: target_high.");

        var unknown = SafetyRuleEditor.SetField(Rule("glucose.any"), "foo", "1");
        unknown.Rule.ShouldBeNull();
        unknown.Error.ShouldBe("У правила glucose.any нет поля foo. Поля: low_urgent, low_alert, high_alert, high_urgent.");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000")]
    [InlineData("4.123")]
    [InlineData("")]
    public void Rejects_bad_numbers(string value)
    {
        var edit = SafetyRuleEditor.SetField(Rule("glucose.any"), "low_alert", value);

        edit.Rule.ShouldBeNull();
        edit.Error.ShouldBe(ValueError);
    }

    [Fact]
    public void Refuses_crossing_thresholds()
    {
        var lowAboveHigh = SafetyRuleEditor.SetField(Rule("glucose.any"), "low_alert", "20");
        lowAboveHigh.Rule.ShouldBeNull();
        lowAboveHigh.Error.ShouldBe(OrderError);

        var highBelowLow = SafetyRuleEditor.SetField(Rule("glucose.any"), "high_urgent", "10");
        highBelowLow.Rule.ShouldBeNull();
        highBelowLow.Error.ShouldBe(OrderError);

        // Equal neighbours are fine.
        var equal = SafetyRuleEditor.SetField(Rule("glucose.any"), "low_alert", "3.0");
        equal.Error.ShouldBeNull();
        equal.Rule.ShouldNotBeNull().LowAlert.ShouldBe(3.0m);
    }

    [Fact]
    public void Symptom_level_is_alert_or_urgent()
    {
        var ok = SafetyRuleEditor.SetField(Rule("symptom.bleeding"), "symptom_level", "ALERT");
        var rule = ok.Rule.ShouldNotBeNull();
        rule.SymptomLevel.ShouldBe("alert");
        rule.Source.ShouldBe(SafetyRuleSources.Doctor);

        var bad = SafetyRuleEditor.SetField(Rule("symptom.bleeding"), "symptom_level", "high");
        bad.Rule.ShouldBeNull();
        bad.Error.ShouldBe("Уровень: alert или urgent.");
    }

    [Fact]
    public void Window_hours_is_an_integer_from_1_to_168()
    {
        SafetyRuleEditor.SetField(Rule("combo.bp_symptoms"), "window_hours", "48").Rule.ShouldNotBeNull().WindowHours.ShouldBe(48);

        foreach (var bad in new[] { "0", "169", "1.5" })
        {
            var edit = SafetyRuleEditor.SetField(Rule("combo.bp_symptoms"), "window_hours", bad);
            edit.Rule.ShouldBeNull();
            edit.Error.ShouldBe("Окно: целое число часов от 1 до 168.");
        }
    }
}
