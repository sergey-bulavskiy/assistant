using System.Text.Json.Nodes;

namespace Assistant.Evals;

public class ExtractionCheckTests
{
    [Fact]
    public void Note_tags_must_match_in_order_and_content()
    {
        const string answer = """{"events":[{"type":"note","day":0,"time":null,"intent":"record","text":"felt more energetic after a short walk","tags":["walk","energy"]}],"unclear":[],"is_question":false}""";
        static string Expected(string tags) => $$"""{"events":[{"type":"note","intent":"record","text":"felt more energetic after a short walk","tags":{{tags}}}],"unclear":[],"alert":null}""";
        ShouldPass(Check(Case(Expected("[\"walk\",\"energy\"]")), answer));
        foreach (var tags in new[] { "[\"energy\",\"walk\"]", "[\"walk\"]", "[\"walk\",\"energy\",\"rest\"]", "[\"walk\",\"rest\"]" })
            ShouldFailWith(Check(Case(Expected(tags)), answer), "missing event");
    }

    private const string Fasting54 =
        """{"events":[{"type":"glucose","value":5.4,"context":"fasting"}],"unclear":[],"is_question":false}""";

    private static EvalCase Case(string expectedJson, bool critical = false) =>
        EvalCase.Parse(
            $$"""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"test","critical":{{(critical ? "true" : "false")}},"expected":{{expectedJson}}}""");

    private static CaseResult Check(EvalCase evalCase, string answer) => ExtractionCheck.Run(evalCase, answer);

    private static string Glucose(string value, string context = "\"fasting\"", string time = "null") =>
        $$"""{"events":[{"type":"glucose","day":0,"time":{{time}},"value":{{value}},"unit":"mmol/L","context":{{context}}}],"unclear":[],"is_question":false}""";

    private static void ShouldPass(CaseResult result)
    {
        result.Passed.ShouldBeTrue(string.Join("; ", result.Problems));
        result.Problems.ShouldBeEmpty();
    }

    private static void ShouldFailWith(CaseResult result, string text)
    {
        result.Passed.ShouldBeFalse();
        result.Problems.ShouldContain(p => p.Contains(text));
    }

    [Fact]
    public void A_matching_answer_passes()
    {
        var c = Case(Fasting54);

        ShouldPass(Check(c, Glucose("5.4")));
        ShouldPass(Check(c, Glucose("\"5,4\"")));
    }

    [Fact]
    public void A_different_value_or_context_fails()
    {
        var c = Case(Fasting54);

        var otherValue = Check(c, Glucose("5.5"));
        ShouldFailWith(otherValue, "missing event");
        ShouldFailWith(otherValue, "unexpected event glucose");
        ShouldFailWith(Check(c, Glucose("5.4", "\"before_meal\"")), "missing event");
    }

    [Fact]
    public void An_extra_or_missing_event_fails()
    {
        var c = Case(Fasting54);
        const string withWeight =
            """{"events":[{"type":"glucose","day":0,"time":null,"value":5.4,"unit":null,"context":"fasting"},{"type":"weight","kg":70}],"unclear":[],"is_question":false}""";

        ShouldFailWith(Check(c, withWeight), "unexpected event weight");
        ShouldFailWith(Check(c, """{"events":[],"unclear":[],"is_question":false}"""), "missing event");
    }

    [Fact]
    public void Events_match_in_any_order()
    {
        var c = Case("""{"events":[{"type":"glucose","value":5.4,"context":"fasting"},{"type":"weight","kg":70}],"unclear":[]}""");
        const string weightFirst =
            """{"events":[{"type":"weight","kg":70},{"type":"glucose","day":0,"time":null,"value":5.4,"unit":null,"context":"fasting"}],"unclear":[]}""";

        ShouldPass(Check(c, weightFirst));
    }

    [Fact]
    public void Stated_time_must_match_and_no_time_must_stay_unstated()
    {
        var withTime = Case("""{"events":[{"type":"glucose","value":5.6,"at":"2030-02-07 08:30"}],"unclear":[]}""");
        var withoutTime = Case("""{"events":[{"type":"glucose","value":5.6}],"unclear":[]}""");

        ShouldPass(Check(withTime, Glucose("5.6", "null", "\"08:30\"")));
        ShouldFailWith(Check(withTime, Glucose("5.6", "null", "\"08:00\"")), "missing event");
        ShouldFailWith(Check(withTime, Glucose("5.6", "null")), "missing event");
        ShouldFailWith(Check(withoutTime, Glucose("5.6", "null", "\"08:30\"")), "missing event");
    }

    [Fact]
    public void Validator_rejections_count_as_unclear()
    {
        var c = Case("""{"events":[],"unclear":["unit"]}""");

        ShouldPass(Check(c, """{"events":[{"type":"glucose","value":126,"unit":"mg/dL"}],"unclear":[]}"""));
        ShouldPass(Check(c, """{"events":[],"unclear":[{"fragment":"126","reason":"unit"}]}"""));
        ShouldFailWith(Check(c, """{"events":[],"unclear":[]}"""), "unclear: expected [unit], got []");
    }

    [Fact]
    public void An_unknown_unclear_reason_counts_as_value()
    {
        var c = Case("""{"events":[],"unclear":["value"]}""");

        ShouldPass(Check(c, """{"events":[],"unclear":[{"fragment":"7","reason":"strange"}]}"""));
    }

    [Fact]
    public void Is_question_is_checked_only_when_stated()
    {
        var stated = Case(Fasting54);
        var notStated = Case("""{"events":[{"type":"glucose","value":5.4,"context":"fasting"}],"unclear":[]}""");
        var question = Glucose("5.4").Replace("\"is_question\":false", "\"is_question\":true");

        ShouldFailWith(Check(stated, question), "is_question");
        ShouldPass(Check(notStated, question));
    }

    [Fact]
    public void The_alert_is_checked_with_the_default_rules()
    {
        var urgent = Case(
            """{"events":[{"type":"glucose","value":2.5}],"unclear":[],"alert":{"rule_key":"glucose.any","level":"urgent"}}""",
            critical: true);
        var wronglyUrgent = Case(
            """{"events":[{"type":"glucose","value":3.5}],"unclear":[],"alert":{"rule_key":"glucose.any","level":"urgent"}}""",
            critical: true);
        var none = Case("""{"events":[{"type":"glucose","value":5.0}],"unclear":[],"alert":null}""", critical: true);

        ShouldPass(Check(urgent, Glucose("2.5", "null")));
        ShouldFailWith(Check(wronglyUrgent, Glucose("3.5", "null")), "alert: expected glucose.any urgent, got glucose.any alert");
        ShouldPass(Check(none, Glucose("5.0", "null")));
        var unexpectedAlert = Check(none, Glucose("2.5", "null"));
        ShouldFailWith(unexpectedAlert, "alert: expected none");
        ShouldFailWith(unexpectedAlert, "missing event");
    }

    [Fact]
    public void Intent_is_checked_only_when_stated()
    {
        var stated = Case("""{"events":[{"type":"glucose","value":5.4,"intent":"question_only"}],"unclear":[]}""");
        var notStated = Case("""{"events":[{"type":"glucose","value":5.4}],"unclear":[]}""");
        var questionOnly = Glucose("5.4").Replace("\"type\":\"glucose\"", "\"type\":\"glucose\",\"intent\":\"question_only\"");
        var record = Glucose("5.4").Replace("\"type\":\"glucose\"", "\"type\":\"glucose\",\"intent\":\"record\"");

        ShouldPass(Check(stated, questionOnly));
        ShouldFailWith(Check(stated, record), "missing event");
        ShouldPass(Check(notStated, record));
        ShouldPass(Check(notStated, questionOnly));
    }

    [Fact]
    public void A_missing_intent_counts_as_unsure_in_a_question_and_as_record_otherwise()
    {
        var unsure = Case("""{"events":[{"type":"glucose","value":5.4,"intent":"unsure"}],"unclear":[]}""");
        var record = Case("""{"events":[{"type":"glucose","value":5.4,"intent":"record"}],"unclear":[]}""");
        var question = Glucose("5.4").Replace("\"is_question\":false", "\"is_question\":true");

        ShouldPass(Check(unsure, question));
        ShouldPass(Check(record, Glucose("5.4")));
        ShouldFailWith(Check(record, question), "missing event");
    }

    [Fact]
    public void Undo_is_checked_only_when_stated()
    {
        var stated = Case("""{"events":[],"unclear":[],"undo":true}""");
        var notStated = Case("""{"events":[],"unclear":[]}""");

        ShouldPass(Check(stated, """{"events":[],"unclear":[],"undo":true}"""));
        ShouldFailWith(Check(stated, """{"events":[],"unclear":[]}"""), "undo: expected True, got False");
        ShouldPass(Check(notStated, """{"events":[],"unclear":[],"undo":true}"""));
    }

    [Fact]
    public void A_question_only_value_still_counts_for_the_alert()
    {
        var c = Case(
            """{"events":[{"type":"glucose","value":2.5,"intent":"question_only"}],"unclear":[],"alert":{"rule_key":"glucose.any","level":"urgent"}}""",
            critical: true);
        var answer = Glucose("2.5", "null").Replace("\"type\":\"glucose\"", "\"type\":\"glucose\",\"intent\":\"question_only\"");

        ShouldPass(Check(c, answer));
    }

    [Theory]
    [InlineData("не JSON")]
    [InlineData("")]
    public void An_unreadable_answer_fails(string answer)
    {
        ShouldFailWith(Check(Case(Fasting54), answer), "not a readable extraction JSON object");
    }

    [Theory]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"x","critical":true,"expected":{"events":[],"unclear":[]}}""")]
    [InlineData("""{"id":"t1","now":"07.02.2030 09:00","time_zone":"UTC","text":"x","critical":false,"expected":{"events":[],"unclear":[]}}""")]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"Nowhere/Nothing","text":"x","critical":false,"expected":{"events":[],"unclear":[]}}""")]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"x","critical":false,"expected":{"events":[{"value":5}],"unclear":[]}}""")]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"x","critical":false,"expected":{"events":[],"unclear":[1]}}""")]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"x","critical":false,"expected":{"events":[{"type":"glucose","intent":"maybe"}],"unclear":[]}}""")]
    [InlineData("""{"id":"t1","now":"2030-02-07T09:00","time_zone":"UTC","text":"x","critical":false,"expected":{"events":[],"unclear":[],"undo":"yes"}}""")]
    [InlineData("{")]
    public void Parse_rejects_bad_cases(string line)
    {
        Should.Throw<FormatException>(() => EvalCase.Parse(line));
    }

    [Fact]
    public void Recorded_output_round_trip()
    {
        var c = Case("""{"events":[],"unclear":[]}""");
        c.RecordedOutput.ShouldBeNull();

        c.SetRecordedOutput("""{"events":[],"unclear":[],"is_question":false}""");
        c.RecordedOutput.ShouldBe("""{"events":[],"unclear":[],"is_question":false}""");
        c.Json["recorded_output"].ShouldBeOfType<JsonObject>();

        c.SetRecordedOutput("""Ответ: {"events":[]}""");
        c.RecordedOutput.ShouldBe("""Ответ: {"events":[]}""");
        c.Json["recorded_output"].ShouldBeAssignableTo<JsonValue>();
    }
}
