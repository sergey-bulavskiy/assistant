using System.Globalization;
using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class SafetyRuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    // ---- thresholds ----

    [Theory]
    [InlineData("3.9", null, null)]
    [InlineData("3.89", "alert", "3.9")]
    [InlineData("3.0", "alert", "3.9")]
    [InlineData("2.99", "urgent", "3.0")]
    [InlineData("0.5", "urgent", "3.0")]
    public void Glucose_low_boundaries(string value, string? level, string? threshold)
    {
        var result = One(Glucose(D(value)));

        if (level is null)
        {
            result.Alert.ShouldBeNull();
            return;
        }

        ShouldDecide(result, "glucose.any", level, D(threshold!));
        result.Alert!.IsLow.ShouldBeTrue();
        result.Alert.Value.ShouldBe(D(value));
        result.Alert.Kind.ShouldBe(SafetyAlertKind.Glucose);
    }

    [Theory]
    [InlineData("10.99", null, null)]
    [InlineData("11.0", "alert", "11.0")]
    [InlineData("13.89", "alert", "11.0")]
    [InlineData("13.9", "urgent", "13.9")]
    [InlineData("35.0", "urgent", "13.9")]
    public void Glucose_high_boundaries(string value, string? level, string? threshold)
    {
        var result = One(Glucose(D(value)));

        if (level is null)
        {
            result.Alert.ShouldBeNull();
            return;
        }

        ShouldDecide(result, "glucose.any", level, D(threshold!));
        result.Alert!.IsLow.ShouldBeFalse();
        result.Alert.Value.ShouldBe(D(value));
    }

    [Fact]
    public void Glucose_in_range_has_no_decision_and_no_flags()
    {
        var result = One(Glucose(5.0m));

        result.Alert.ShouldBeNull();
        result.Flags.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("fasting", "5.1", true)]
    [InlineData("fasting", "5.09", false)]
    [InlineData("before_meal", "5.1", true)]
    [InlineData("bedtime", "5.1", true)]
    [InlineData("night", "5.1", true)]
    [InlineData("after_meal_1h", "7.0", true)]
    [InlineData("after_meal_1h", "6.99", false)]
    [InlineData("after_meal_2h", "6.7", true)]
    [InlineData("after_meal_2h", "6.69", false)]
    [InlineData("other", "9.0", false)]
    public void Out_of_target_follows_the_context(string context, string value, bool flagged)
    {
        var result = One(Glucose(D(value), context));

        result.Alert.ShouldBeNull();
        if (flagged)
        {
            result.Flags.ShouldBe(new[] { "out_of_target" });
        }
        else
        {
            result.Flags.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Out_of_target_and_an_alert_can_both_apply()
    {
        var result = One(Glucose(12.0m, "fasting"));

        result.Flags.ShouldBe(new[] { "out_of_target" });
        ShouldDecide(result, "glucose.any", "alert", 11.0m);
    }

    [Theory]
    [InlineData(139, 89, null, null, null)]
    [InlineData(140, 80, "blood_pressure.systolic", "alert", 140)]
    [InlineData(120, 90, "blood_pressure.diastolic", "alert", 90)]
    [InlineData(159, 109, "blood_pressure.systolic", "alert", 140)]
    [InlineData(160, 100, "blood_pressure.systolic", "urgent", 160)]
    [InlineData(150, 110, "blood_pressure.diastolic", "urgent", 110)]
    [InlineData(165, 115, "blood_pressure.systolic", "urgent", 160)]
    public void Blood_pressure_boundaries(int systolic, int diastolic, string? key, string? level, int? threshold)
    {
        var result = One(Pressure(systolic, diastolic));

        if (key is null)
        {
            result.Alert.ShouldBeNull();
            return;
        }

        ShouldDecide(result, key, level!, threshold!.Value);
        var systolicSide = key == "blood_pressure.systolic";
        result.Alert!.Kind.ShouldBe(systolicSide ? SafetyAlertKind.Systolic : SafetyAlertKind.Diastolic);
        result.Alert.Value.ShouldBe(systolicSide ? systolic : diastolic);
        result.Flags.ShouldBeEmpty();
    }

    // ---- symptoms ----

    [Theory]
    [InlineData("vision_disturbance", "alert")]
    [InlineData("epigastric_pain", "alert")]
    [InlineData("bleeding", "urgent")]
    [InlineData("seizure", "urgent")]
    [InlineData("headache", null)]
    [InlineData("swelling", null)]
    [InlineData("nausea_vomiting", null)]
    [InlineData("other", null)]
    public void Symptom_levels_come_from_the_rules(string code, string? level)
    {
        var result = One(Symptom(code));

        if (level is null)
        {
            result.Alert.ShouldBeNull();
            return;
        }

        ShouldDecide(result, "symptom." + code, level, null);
        result.Alert!.Kind.ShouldBe(SafetyAlertKind.Symptom);
        result.Alert.SymptomCode.ShouldBe(code);
    }

    // ---- combination ----

    [Fact]
    public void Pressure_and_symptom_in_one_message_alert_once_on_the_pressure()
    {
        var results = Evaluate(Pressure(150, 95), Symptom("headache"));

        ShouldDecide(results[0], "combo.bp_symptoms", "urgent", null);
        results[0].Alert!.Kind.ShouldBe(SafetyAlertKind.Combo);
        results[0].Alert!.Systolic.ShouldBe(150);
        results[0].Alert!.Diastolic.ShouldBe(95);
        results[0].Alert!.SymptomCode.ShouldBe("headache");
        results[0].Alert!.ThresholdSource.ShouldBe("guideline_default");
        results[1].Alert.ShouldBeNull();

        var reversed = Evaluate(Symptom("headache"), Pressure(150, 95));

        reversed[0].Alert.ShouldBeNull();
        ShouldDecide(reversed[1], "combo.bp_symptoms", "urgent", null);
    }

    [Fact]
    public void Symptom_after_an_earlier_pressure_reading_is_urgent()
    {
        var result = One(Symptom("headache"), recent: new[] { Saved(50, Pressure(150, 95, Now.AddHours(-3))) });

        ShouldDecide(result, "combo.bp_symptoms", "urgent", null);
        result.Alert!.Systolic.ShouldBe(150);
        result.Alert.Diastolic.ShouldBe(95);
        result.Alert.SymptomCode.ShouldBe("headache");
    }

    [Fact]
    public void Pressure_after_an_earlier_symptom_is_urgent()
    {
        var result = One(Pressure(145, 85), recent: new[] { Saved(51, Symptom("vision_disturbance", Now.AddHours(-2))) });

        ShouldDecide(result, "combo.bp_symptoms", "urgent", null);
        result.Alert!.SymptomCode.ShouldBe("vision_disturbance");
    }

    [Fact]
    public void The_window_is_inclusive_and_editable()
    {
        One(Symptom("headache"), recent: new[] { Saved(1, Pressure(150, 95, Now.AddHours(-24))) })
            .Alert!.RuleKey.ShouldBe("combo.bp_symptoms");
        One(Symptom("headache"), recent: new[] { Saved(1, Pressure(150, 95, Now.AddHours(-24).AddMinutes(-1))) })
            .Alert.ShouldBeNull();

        var sixHours = RulesWith("combo.bp_symptoms", r => r with { WindowHours = 6, Source = "doctor" });

        One(Symptom("headache"), sixHours, new[] { Saved(1, Pressure(150, 95, Now.AddHours(-6))) })
            .Alert!.RuleKey.ShouldBe("combo.bp_symptoms");
        One(Symptom("headache"), sixHours, new[] { Saved(1, Pressure(150, 95, Now.AddHours(-7))) })
            .Alert.ShouldBeNull();
    }

    [Fact]
    public void The_combination_needs_pressure_at_alert_level()
    {
        One(Symptom("headache"), recent: new[] { Saved(1, Pressure(139, 89, Now.AddHours(-1))) })
            .Alert.ShouldBeNull();

        var lowered = RulesWith("blood_pressure.systolic", r => r with { HighAlert = 130m, Source = "doctor" });

        One(Symptom("headache"), lowered, new[] { Saved(1, Pressure(135, 85, Now.AddHours(-1))) })
            .Alert!.RuleKey.ShouldBe("combo.bp_symptoms");
    }

    [Theory]
    [InlineData(140, 80, true)]
    [InlineData(120, 90, true)]
    [InlineData(139, 89, false)]
    public void The_combination_starts_exactly_at_the_pressure_alert_level(int systolic, int diastolic, bool combined)
    {
        var result = One(Symptom("headache"), recent: new[] { Saved(1, Pressure(systolic, diastolic, Now.AddHours(-1))) });

        if (combined)
        {
            ShouldDecide(result, "combo.bp_symptoms", "urgent", null);
        }
        else
        {
            result.Alert.ShouldBeNull();
        }
    }

    [Fact]
    public void Without_an_alert_level_the_combination_uses_the_urgent_level()
    {
        var noAlertLevel = RulesWith("blood_pressure.systolic", r => r with { HighAlert = null, Source = "doctor" });

        One(Symptom("headache"), noAlertLevel, new[] { Saved(1, Pressure(150, 85, Now.AddHours(-1))) })
            .Alert.ShouldBeNull();
        One(Symptom("headache"), noAlertLevel, new[] { Saved(1, Pressure(160, 85, Now.AddHours(-1))) })
            .Alert!.RuleKey.ShouldBe("combo.bp_symptoms");
    }

    [Fact]
    public void The_nearest_pressure_reading_is_the_partner()
    {
        var recent = new[]
        {
            Saved(1, Pressure(170, 100, Now.AddHours(-10))),
            Saved(2, Pressure(145, 92, Now.AddHours(-2)))
        };

        var result = One(Symptom("epigastric_pain"), recent: recent);

        ShouldDecide(result, "combo.bp_symptoms", "urgent", null);
        result.Alert!.Systolic.ShouldBe(145);
        result.Alert.Diastolic.ShouldBe(92);
    }

    [Fact]
    public void Swelling_never_makes_the_combination()
    {
        var results = Evaluate(Pressure(150, 95), Symptom("swelling"));

        ShouldDecide(results[0], "blood_pressure.systolic", "alert", 140m);
        results[1].Alert.ShouldBeNull();

        var later = One(Pressure(150, 95), recent: new[] { Saved(1, Symptom("swelling", Now.AddHours(-1))) });

        ShouldDecide(later, "blood_pressure.systolic", "alert", 140m);
    }

    [Fact]
    public void Combination_label_is_the_doctors_only_when_every_rule_used_is()
    {
        var comboAndSystolic = WithDoctorSource(Defaults(), "combo.bp_symptoms", "blood_pressure.systolic");

        // Diastolic 85 is below its alert level, so only the systolic rule was used.
        Evaluate(comboAndSystolic, Pressure(150, 85), Symptom("headache"))[0]
            .Alert!.ThresholdSource.ShouldBe("doctor");
        // Diastolic 95 reached the (default) diastolic rule too.
        Evaluate(comboAndSystolic, Pressure(150, 95), Symptom("headache"))[0]
            .Alert!.ThresholdSource.ShouldBe("guideline_default");

        var pressureOnly = WithDoctorSource(Defaults(), "blood_pressure.systolic", "blood_pressure.diastolic");

        var result = Evaluate(pressureOnly, Pressure(150, 95), Symptom("headache"))[0];
        result.Alert!.RuleKey.ShouldBe("combo.bp_symptoms");
        result.Alert.ThresholdSource.ShouldBe("guideline_default");
    }

    // ---- rule data ----

    [Fact]
    public void Doctor_values_and_labels_are_used()
    {
        var doctor = RulesWith("glucose.any", r => r with { LowAlert = 4.0m, Source = "doctor" });

        var low = One(Glucose(3.95m), doctor);
        ShouldDecide(low, "glucose.any", "alert", 4.0m);
        low.Alert!.ThresholdSource.ShouldBe("doctor");
        One(Glucose(4.0m), doctor).Alert.ShouldBeNull();

        One(Glucose(3.5m)).Alert!.ThresholdSource.ShouldBe("guideline_default");
    }

    [Fact]
    public void A_missing_rule_or_field_is_not_checked()
    {
        var noGlucoseRule = One(Glucose(2.5m), RulesWith("glucose.any", _ => null));
        noGlucoseRule.Alert.ShouldBeNull();
        noGlucoseRule.Flags.ShouldBeEmpty();

        ShouldDecide(One(Glucose(2.5m), RulesWith("glucose.any", r => r with { LowUrgent = null })), "glucose.any", "alert", 3.9m);

        One(Symptom("bleeding"), RulesWith("symptom.bleeding", _ => null)).Alert.ShouldBeNull();
        One(Symptom("bleeding"), RulesWith("symptom.bleeding", r => r with { SymptomLevel = null })).Alert.ShouldBeNull();

        var noCombo = Evaluate(RulesWith("combo.bp_symptoms", _ => null), Pressure(150, 95), Symptom("headache"));
        ShouldDecide(noCombo[0], "blood_pressure.systolic", "alert", 140m);

        One(Glucose(6.0m, "fasting"), RulesWith("glucose.fasting", _ => null)).Flags.ShouldBeEmpty();
    }

    [Fact]
    public void An_owner_can_change_a_symptom_level()
    {
        var result = One(Symptom("bleeding"), RulesWith("symptom.bleeding", r => r with { SymptomLevel = "alert", Source = "doctor" }));

        ShouldDecide(result, "symptom.bleeding", "alert", null);
        result.Alert!.ThresholdSource.ShouldBe("doctor");
    }

    // ---- age ----

    [Fact]
    public void A_reading_exactly_at_the_age_limit_still_alerts()
    {
        var result = One(Glucose(2.5m, at: Now.AddHours(-12)));

        ShouldDecide(result, "glucose.any", "urgent", 3.0m);
        result.Flags.ShouldBeEmpty();
    }

    [Fact]
    public void A_reading_just_past_the_age_limit_is_flagged_not_alerted()
    {
        var result = One(Glucose(2.5m, at: Now.AddHours(-12).AddSeconds(-1)));

        result.Alert.ShouldBeNull();
        result.Flags.ShouldBe(new[] { "old_value_not_alerted" });
    }

    [Fact]
    public void An_old_reading_keeps_its_target_flag()
    {
        var result = One(Glucose(12.0m, "fasting", Now.AddHours(-13)));

        result.Alert.ShouldBeNull();
        result.Flags.ShouldBe(new[] { "out_of_target", "old_value_not_alerted" });
    }

    [Fact]
    public void An_old_reading_without_an_alert_level_gets_no_age_flag()
    {
        One(Glucose(5.0m, at: Now.AddHours(-13))).Flags.ShouldBeEmpty();
    }

    [Fact]
    public void A_reading_slightly_in_the_future_is_treated_normally()
    {
        ShouldDecide(One(Glucose(2.5m, at: Now.AddMinutes(10))), "glucose.any", "urgent", 3.0m);
    }

    [Fact]
    public void An_old_pressure_reading_does_not_make_a_combination_alert()
    {
        var result = One(
            Pressure(150, 95, Now.AddHours(-13)),
            recent: new[] { Saved(7, Symptom("headache", Now.AddHours(-13))) });

        result.Alert.ShouldBeNull();
        result.Flags.ShouldBe(new[] { "old_value_not_alerted" });

        // Pressure only, in one message: still flagged, not alerted.
        var pressureOnly = Evaluate(Pressure(165, 100, Now.AddHours(-13))).ShouldHaveSingleItem();
        pressureOnly.Alert.ShouldBeNull();
        pressureOnly.Flags.ShouldBe(new[] { "old_value_not_alerted" });
    }

    [Fact]
    public void A_new_symptom_pairs_with_an_old_pressure_reading_of_the_same_message()
    {
        var results = Evaluate(Pressure(150, 95, Now.AddHours(-13)), Symptom("headache"));

        results[0].Alert.ShouldBeNull();
        results[0].Flags.ShouldBe(new[] { "old_value_not_alerted" });
        ShouldDecide(results[1], "combo.bp_symptoms", "urgent", null);
        results[1].Alert!.Systolic.ShouldBe(150);
        results[1].Alert!.Diastolic.ShouldBe(95);
        results[1].Alert!.SymptomCode.ShouldBe("headache");
    }

    [Fact]
    public void An_old_pressure_reading_of_the_same_message_still_needs_the_window_and_the_alert_level()
    {
        Evaluate(Pressure(150, 95, Now.AddHours(-25)), Symptom("headache"))[1].Alert.ShouldBeNull();
        Evaluate(Pressure(135, 85, Now.AddHours(-13)), Symptom("headache"))[1].Alert.ShouldBeNull();
    }

    // ---- several events and edge payloads ----

    [Fact]
    public void Each_event_gets_its_own_evaluation_in_order()
    {
        var results = Evaluate(Glucose(2.5m), Pressure(165, 100), Glucose(5.0m));

        results.Count.ShouldBe(3);
        ShouldDecide(results[0], "glucose.any", "urgent", 3.0m);
        ShouldDecide(results[1], "blood_pressure.systolic", "urgent", 160m);
        results[2].Alert.ShouldBeNull();
    }

    [Fact]
    public void Insulin_and_unreadable_payloads_are_never_checked()
    {
        var insulin = One(new NewHealthEvent("insulin", Now, "message", "{\"kind\":\"short\",\"name\":null,\"units\":100}"));
        insulin.Alert.ShouldBeNull();
        insulin.Flags.ShouldBeEmpty();

        var emptyGlucose = One(new NewHealthEvent("glucose", Now, "message", "{}"));
        emptyGlucose.Alert.ShouldBeNull();
        emptyGlucose.Flags.ShouldBeEmpty();

        One(new NewHealthEvent("glucose", Now, "message", "not json")).Alert.ShouldBeNull();
        One(new NewHealthEvent("blood_pressure", Now, "message", "{}")).Alert.ShouldBeNull();
    }

    [Fact]
    public void MostSevere_takes_the_first_urgent_else_the_first_alert()
    {
        var a = new SafetyDecision("a", "alert", SafetyAlertKind.Glucose, "guideline_default");
        var b = new SafetyDecision("b", "urgent", SafetyAlertKind.Glucose, "guideline_default");
        var c = new SafetyDecision("c", "urgent", SafetyAlertKind.Glucose, "guideline_default");

        SafetyRuleEvaluator.MostSevere(new[] { a, null, b, c }).ShouldBe(b);
        SafetyRuleEvaluator.MostSevere(new[] { a, c }).ShouldBe(c);
        SafetyRuleEvaluator.MostSevere(new[] { a }).ShouldBe(a);
        SafetyRuleEvaluator.MostSevere(new SafetyDecision?[] { null }).ShouldBeNull();
        SafetyRuleEvaluator.MostSevere(Array.Empty<SafetyDecision?>()).ShouldBeNull();
    }

    [Fact]
    public void Every_key_the_rules_read_is_seeded()
    {
        var seeded = SafetyRuleDefaults.All.Select(r => r.RuleKey).ToList();

        seeded.ShouldContain(SafetyRuleKeys.GlucoseAny);
        seeded.ShouldContain(SafetyRuleKeys.GlucoseFasting);
        seeded.ShouldContain(SafetyRuleKeys.GlucoseAfter1h);
        seeded.ShouldContain(SafetyRuleKeys.GlucoseAfter2h);
        seeded.ShouldContain(SafetyRuleKeys.BloodPressureSystolic);
        seeded.ShouldContain(SafetyRuleKeys.BloodPressureDiastolic);
        seeded.ShouldContain(SafetyRuleKeys.ComboBpSymptoms);

        foreach (var key in seeded.Where(k => k.StartsWith(SafetyRuleKeys.SymptomPrefix, StringComparison.Ordinal)))
        {
            SymptomCodes.All.ShouldContain(key[SafetyRuleKeys.SymptomPrefix.Length..]);
        }

        SafetyRuleEvaluator.ComboSymptomCodes.ShouldAllBe(code => SymptomCodes.All.Contains(code));
        SafetyRuleEvaluator.ComboSymptomCodes.ShouldNotContain("swelling");
    }

    // ---- helpers ----

    private static void ShouldDecide(SafetyEvaluation result, string ruleKey, string level, decimal? threshold)
    {
        result.Alert.ShouldNotBeNull();
        result.Alert.RuleKey.ShouldBe(ruleKey);
        result.Alert.Level.ShouldBe(level);
        result.Alert.Threshold.ShouldBe(threshold);
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static NewHealthEvent Glucose(decimal value, string context = "other", DateTimeOffset? at = null) =>
        new("glucose", at ?? Now, "message", HealthEventPayloads.Serialize(new GlucosePayload(value, context)));

    private static NewHealthEvent Pressure(int systolic, int diastolic, DateTimeOffset? at = null) =>
        new("blood_pressure", at ?? Now, "message", HealthEventPayloads.Serialize(new BloodPressurePayload(systolic, diastolic, null)));

    private static NewHealthEvent Symptom(string code, DateTimeOffset? at = null) =>
        new("symptom", at ?? Now, "message", HealthEventPayloads.Serialize(new SymptomPayload(code, "test")));

    private static HealthEventInfo Saved(long id, NewHealthEvent e) => new(id, e.Type, e.OccurredAt, e.PayloadJson, null);

    private static List<SafetyRuleInfo> Defaults() => SafetyRuleDefaults.All.ToList();

    private static List<SafetyRuleInfo> RulesWith(string key, Func<SafetyRuleInfo, SafetyRuleInfo?> change)
    {
        var rules = new List<SafetyRuleInfo>();
        foreach (var rule in Defaults())
        {
            var replacement = rule.RuleKey == key ? change(rule) : rule;
            if (replacement is not null)
            {
                rules.Add(replacement);
            }
        }

        return rules;
    }

    private static List<SafetyRuleInfo> WithDoctorSource(List<SafetyRuleInfo> rules, params string[] keys) =>
        rules.Select(r => keys.Contains(r.RuleKey) ? r with { Source = "doctor" } : r).ToList();

    private static SafetyEvaluation One(
        NewHealthEvent e,
        IReadOnlyList<SafetyRuleInfo>? rules = null,
        IReadOnlyList<HealthEventInfo>? recent = null,
        DateTimeOffset? now = null)
    {
        var results = SafetyRuleEvaluator.Evaluate(new[] { e }, recent ?? Array.Empty<HealthEventInfo>(), rules ?? Defaults(), now ?? Now);
        return results.ShouldHaveSingleItem();
    }

    private static IReadOnlyList<SafetyEvaluation> Evaluate(params NewHealthEvent[] events) => Evaluate(Defaults(), events);

    private static IReadOnlyList<SafetyEvaluation> Evaluate(IReadOnlyList<SafetyRuleInfo> rules, params NewHealthEvent[] events)
    {
        var results = SafetyRuleEvaluator.Evaluate(events, Array.Empty<HealthEventInfo>(), rules, Now);
        results.Count.ShouldBe(events.Length);
        return results;
    }
}
