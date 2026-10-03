using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Deterministic safety rules (pure: no I/O, no model, no side effects): new events + the
/// profile's rules + recent active events → one SafetyEvaluation per new event, in order. Every
/// threshold comes from the rules passed in (the family's safety_rules rows); a missing rule or a
/// null field is simply not checked, with no fallback to code values. "Low" fires when value &lt;
/// threshold, "high" when value ≥ threshold. The most severe decision of an event wins: urgent over
/// alert, and at the same level the first of combination, glucose.any, systolic, diastolic, symptom.</summary>
public static class SafetyRuleEvaluator
{
    /// <summary>Readings older than this are recorded and flagged, never alerted.</summary>
    public static readonly TimeSpan AlertMaxAge = TimeSpan.FromHours(12);

    /// <summary>Symptoms that, with blood pressure at alert level within the rule's window, make the
    /// combination urgent. swelling is deliberately not one of them.</summary>
    public static IReadOnlyList<string> ComboSymptomCodes { get; } =
        new[] { SymptomCodes.Headache, SymptomCodes.VisionDisturbance, SymptomCodes.EpigastricPain };

    public static IReadOnlyList<SafetyEvaluation> Evaluate(
        IReadOnlyList<NewHealthEvent> newEvents,
        IReadOnlyList<HealthEventInfo> recentEvents,
        IReadOnlyList<SafetyRuleInfo> rules,
        DateTimeOffset utcNow)
    {
        var batch = newEvents.Select(e => Reading.From(e.Type, e.OccurredAt, e.PayloadJson)).ToArray();
        var recent = recentEvents.Select(e => Reading.From(e.Type, e.OccurredAt, e.PayloadJson)).ToArray();

        var results = new List<SafetyEvaluation>(batch.Length);
        foreach (var reading in batch)
        {
            var flags = new List<string>();
            if (IsOutOfTarget(reading, rules))
            {
                flags.Add(HealthEventFlags.OutOfTarget);
            }

            var decision = MostSevere(Candidates(reading, batch, recent, rules, utcNow));
            if (decision is not null && utcNow - reading.OccurredAt > AlertMaxAge)
            {
                flags.Add(HealthEventFlags.OldValueNotAlerted);
                decision = null;
            }

            results.Add(new SafetyEvaluation(flags, decision));
        }

        return results;
    }

    /// <summary>The first urgent decision, else the first alert, else null.</summary>
    public static SafetyDecision? MostSevere(IEnumerable<SafetyDecision?> decisions)
    {
        var list = decisions.OfType<SafetyDecision>().ToList();
        return list.FirstOrDefault(d => d.Level == SafetyAlertLevels.Urgent)
               ?? list.FirstOrDefault(d => d.Level == SafetyAlertLevels.Alert);
    }

    // Fixed order: combination, glucose.any, systolic, diastolic, symptom.
    private static IEnumerable<SafetyDecision?> Candidates(
        Reading reading, Reading[] batch, Reading[] recent, IReadOnlyList<SafetyRuleInfo> rules, DateTimeOffset utcNow)
    {
        yield return Combo(reading, batch, recent, rules, utcNow);

        if (reading.Glucose is { } glucose)
        {
            yield return Threshold(Find(rules, SafetyRuleKeys.GlucoseAny), glucose.Value, SafetyAlertKind.Glucose);
        }

        if (reading.Pressure is { } pressure)
        {
            yield return Threshold(Find(rules, SafetyRuleKeys.BloodPressureSystolic), pressure.Systolic, SafetyAlertKind.Systolic);
            yield return Threshold(Find(rules, SafetyRuleKeys.BloodPressureDiastolic), pressure.Diastolic, SafetyAlertKind.Diastolic);
        }

        if (reading.Symptom is { } symptom)
        {
            yield return SymptomDecision(Find(rules, SafetyRuleKeys.SymptomPrefix + symptom.Code), symptom.Code);
        }
    }

    // Urgent levels are checked before alert levels, so a value past both gets the urgent one.
    private static SafetyDecision? Threshold(SafetyRuleInfo? rule, decimal value, SafetyAlertKind kind)
    {
        if (rule is null)
        {
            return null;
        }

        if (rule.LowUrgent is { } lowUrgent && value < lowUrgent)
        {
            return Decision(rule, SafetyAlertLevels.Urgent, kind, lowUrgent, value, isLow: true);
        }

        if (rule.HighUrgent is { } highUrgent && value >= highUrgent)
        {
            return Decision(rule, SafetyAlertLevels.Urgent, kind, highUrgent, value, isLow: false);
        }

        if (rule.LowAlert is { } lowAlert && value < lowAlert)
        {
            return Decision(rule, SafetyAlertLevels.Alert, kind, lowAlert, value, isLow: true);
        }

        if (rule.HighAlert is { } highAlert && value >= highAlert)
        {
            return Decision(rule, SafetyAlertLevels.Alert, kind, highAlert, value, isLow: false);
        }

        return null;
    }

    private static SafetyDecision Decision(
        SafetyRuleInfo rule, string level, SafetyAlertKind kind, decimal threshold, decimal value, bool isLow) =>
        new(rule.RuleKey, level, kind, SourceOf(rule), Threshold: threshold, Value: value, IsLow: isLow);

    private static SafetyDecision? SymptomDecision(SafetyRuleInfo? rule, string code)
    {
        var level = rule?.SymptomLevel switch
        {
            SymptomLevels.Urgent => SafetyAlertLevels.Urgent,
            SymptomLevels.Alert => SafetyAlertLevels.Alert,
            _ => null
        };
        return rule is null || level is null
            ? null
            : new SafetyDecision(rule.RuleKey, level, SafetyAlertKind.Symptom, SourceOf(rule), SymptomCode: code);
    }

    private static SafetyDecision? Combo(
        Reading reading, Reading[] batch, Reading[] recent, IReadOnlyList<SafetyRuleInfo> rules, DateTimeOffset utcNow)
    {
        var combo = Find(rules, SafetyRuleKeys.ComboBpSymptoms);
        if (combo is null || combo.WindowHours is not { } hours || hours <= 0)
        {
            return null;
        }

        var window = TimeSpan.FromHours(hours);
        var systolicRule = Find(rules, SafetyRuleKeys.BloodPressureSystolic);
        var diastolicRule = Find(rules, SafetyRuleKeys.BloodPressureDiastolic);

        if (reading.Pressure is { } pressure)
        {
            var reached = RulesAtAlertLevel(pressure, systolicRule, diastolicRule);
            if (reached.Count == 0)
            {
                return null;
            }

            // Symptoms of the same message or of earlier saved events, nearest in time first.
            var partner = Nearest(reading, batch.Concat(recent).Where(IsComboSymptom), window);
            return partner?.Symptom is { } symptom ? ComboDecision(combo, reached, pressure, symptom.Code) : null;
        }

        if (reading.Symptom is { } own && ComboSymptomCodes.Contains(own.Code))
        {
            // Earlier saved pressure readings, and pressure readings of the same message that are too
            // old to alert themselves: a pair inside one message is otherwise reported once, on its
            // pressure reading (above). The old reading stays flagged, not alerted; this alert is
            // about the new symptom.
            var partners = recent.Concat(batch.Where(r => utcNow - r.OccurredAt > AlertMaxAge));
            var partner = Nearest(
                reading,
                partners.Where(r => r.Pressure is { } p && RulesAtAlertLevel(p, systolicRule, diastolicRule).Count > 0),
                window);
            return partner?.Pressure is { } earlier
                ? ComboDecision(combo, RulesAtAlertLevel(earlier, systolicRule, diastolicRule), earlier, own.Code)
                : null;
        }

        return null;
    }

    private static List<SafetyRuleInfo> RulesAtAlertLevel(
        BloodPressurePayload pressure, SafetyRuleInfo? systolicRule, SafetyRuleInfo? diastolicRule)
    {
        var reached = new List<SafetyRuleInfo>();
        if (systolicRule is not null && AlertLevel(systolicRule) is { } systolicLevel && pressure.Systolic >= systolicLevel)
        {
            reached.Add(systolicRule);
        }

        if (diastolicRule is not null && AlertLevel(diastolicRule) is { } diastolicLevel && pressure.Diastolic >= diastolicLevel)
        {
            reached.Add(diastolicRule);
        }

        return reached;
    }

    // The rule's alert level, or its urgent level when no alert level is set.
    private static decimal? AlertLevel(SafetyRuleInfo rule) => rule.HighAlert ?? rule.HighUrgent;

    private static SafetyDecision ComboDecision(
        SafetyRuleInfo combo, IReadOnlyList<SafetyRuleInfo> reached, BloodPressurePayload pressure, string symptomCode)
    {
        // "порог от врача" only when the combination rule and every pressure rule it used are the doctor's.
        var source = combo.Source == SafetyRuleSources.Doctor && reached.All(r => r.Source == SafetyRuleSources.Doctor)
            ? SafetyRuleSources.Doctor
            : SafetyRuleSources.GuidelineDefault;
        return new SafetyDecision(
            combo.RuleKey, SafetyAlertLevels.Urgent, SafetyAlertKind.Combo, source,
            Systolic: pressure.Systolic, Diastolic: pressure.Diastolic, SymptomCode: symptomCode);
    }

    private static bool IsComboSymptom(Reading reading) =>
        reading.Symptom is { } symptom && ComboSymptomCodes.Contains(symptom.Code);

    // Nearest in time within the window (inclusive); OrderBy is stable, so a tie keeps list order.
    private static Reading? Nearest(Reading reading, IEnumerable<Reading> others, TimeSpan window) =>
        others
            .Select(other => (Other: other, Gap: (other.OccurredAt - reading.OccurredAt).Duration()))
            .Where(x => x.Gap <= window)
            .OrderBy(x => x.Gap)
            .Select(x => x.Other)
            .FirstOrDefault();

    private static bool IsOutOfTarget(Reading reading, IReadOnlyList<SafetyRuleInfo> rules)
    {
        if (reading.Glucose is not { } glucose)
        {
            return false;
        }

        var key = glucose.Context switch
        {
            GlucoseContexts.Fasting or GlucoseContexts.BeforeMeal or GlucoseContexts.Bedtime or GlucoseContexts.Night => SafetyRuleKeys.GlucoseFasting,
            GlucoseContexts.AfterMeal1h => SafetyRuleKeys.GlucoseAfter1h,
            GlucoseContexts.AfterMeal2h => SafetyRuleKeys.GlucoseAfter2h,
            _ => null
        };
        return key is not null && Find(rules, key)?.TargetHigh is { } target && glucose.Value >= target;
    }

    private static SafetyRuleInfo? Find(IReadOnlyList<SafetyRuleInfo> rules, string key) =>
        rules.FirstOrDefault(r => r.RuleKey == key);

    // Anything that is not exactly "doctor" counts as an unconfirmed default.
    private static string SourceOf(SafetyRuleInfo rule) =>
        rule.Source == SafetyRuleSources.Doctor ? SafetyRuleSources.Doctor : SafetyRuleSources.GuidelineDefault;

    /// <summary>One event's payload, read defensively: an unreadable or incomplete payload checks nothing.</summary>
    private sealed record Reading(DateTimeOffset OccurredAt, GlucosePayload? Glucose, BloodPressurePayload? Pressure, SymptomPayload? Symptom)
    {
        public static Reading From(string type, DateTimeOffset occurredAt, string payloadJson)
        {
            GlucosePayload? glucose = null;
            BloodPressurePayload? pressure = null;
            SymptomPayload? symptom = null;
            switch (type)
            {
                case HealthEventTypes.Glucose:
                    var g = HealthEventPayloads.TryDeserialize<GlucosePayload>(payloadJson);
                    glucose = g is not null && g.Value > 0 && !string.IsNullOrEmpty(g.Context) ? g : null;
                    break;
                case HealthEventTypes.BloodPressure:
                    var p = HealthEventPayloads.TryDeserialize<BloodPressurePayload>(payloadJson);
                    pressure = p is not null && p.Systolic > 0 && p.Diastolic > 0 ? p : null;
                    break;
                case HealthEventTypes.Symptom:
                    var s = HealthEventPayloads.TryDeserialize<SymptomPayload>(payloadJson);
                    symptom = s is not null && !string.IsNullOrEmpty(s.Code) ? s : null;
                    break;
            }

            return new Reading(occurredAt, glucose, pressure, symptom);
        }
    }
}
