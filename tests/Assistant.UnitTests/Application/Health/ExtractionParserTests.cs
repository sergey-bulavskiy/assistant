using System.Text.Json.Nodes;
using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class ExtractionParserTests
{
    [Fact]
    public void Identical_known_candidates_with_separately_decoded_tags_keep_first_occurrence_only()
    {
        var output = ExtractionParser.Parse("""
            {"events":[
              {"type":"glucose","intent":"record","value":6.4,"unit":"mmol/L","day":0,"time":"09:30"},
              {"type":"note","intent":"record","text":"synthetic observation","tags":["Walk","Energy"]},
              {"type":"glucose","intent":"record","value":6.4,"unit":"mmol/L","day":0,"time":"09:30"},
              {"type":"note","intent":"record","text":"synthetic observation","tags":["Walk","Energy"]}
            ]}
            """).ShouldNotBeNull();
        output.Events.Select(e => e.Type).ShouldBe(new[] { "glucose", "note" });
        output.Events[0].Value.ShouldBe(6.4m); output.Events[0].Time.ShouldBe("09:30");
        output.Events[1].Text.ShouldBe("synthetic observation");
        output.Events[1].Tags.ShouldBe(new[] { "Walk", "Energy" });
    }

    [Theory]
    [InlineData("type", "insulin")]
    [InlineData("intent", "question_only")]
    [InlineData("intent", "unsure")]
    [InlineData("day", "1")]
    [InlineData("time", "09:31")]
    [InlineData("value", "6.5")]
    [InlineData("unit", "mg/dL")]
    [InlineData("context", "after_meal_1h")]
    [InlineData("units", "0.4")]
    [InlineData("kind", "synthetic second kind")]
    [InlineData("name", "synthetic second name")]
    [InlineData("meal_kind", "dinner")]
    [InlineData("description", "synthetic second description")]
    [InlineData("code", "synthetic second code")]
    [InlineData("text", "synthetic second text")]
    [InlineData("kg", "64.6")]
    [InlineData("systolic", "121")]
    [InlineData("diastolic", "81")]
    [InlineData("pulse", "71")]
    public void Different_decoded_health_scalar_fields_remain_distinct(string field, string value)
    {
        var first = JsonNode.Parse("""
            {"type":"glucose","intent":"record","day":0,"time":"09:30","value":6.4,"unit":"mmol/L",
             "context":"fasting","units":0.3,"kind":"synthetic kind","name":"synthetic name",
             "meal_kind":"lunch","description":"synthetic description","code":"synthetic code",
             "text":"synthetic text","kg":64.5,"systolic":120,"diastolic":80,"pulse":70}
            """)!;
        var second = first.DeepClone();
        second[field] = field is "day" or "value" or "units" or "kg" or "systolic" or "diastolic" or "pulse"
            ? JsonNode.Parse(value) : JsonValue.Create(value);
        var output = ExtractionParser.Parse(new JsonObject { ["events"] = new JsonArray(first, second) }.ToJsonString())
            .ShouldNotBeNull();
        output.Events.Count.ShouldBe(2);
        output.Events[0].Time.ShouldBe("09:30");
        output.Events[0].Value.ShouldBe(6.4m);
    }

    [Theory]
    [InlineData("null", "[]")]
    [InlineData("""["Walk","Energy"]""", """["Energy","Walk"]""")]
    [InlineData("""["Walk"]""", """["walk"]""")]
    [InlineData("""["Walk"]""", """["Energy"]""")]
    public void Tag_nullness_order_case_and_content_are_not_fuzzy_duplicates(string first, string second)
    {
        JsonObject Note(string tags) => new()
        { ["type"] = "note", ["text"] = "synthetic note", ["tags"] = JsonNode.Parse(tags) };
        var output = ExtractionParser.Parse(new JsonObject
        { ["events"] = new JsonArray(Note(first), Note(second)) }.ToJsonString()).ShouldNotBeNull();
        output.Events.Count.ShouldBe(2);
        output.Events.Select(e => e.Text).ShouldBe(new[] { "synthetic note", "synthetic note" });
    }

    [Fact]
    public void Known_candidate_dedup_preserves_uncertainty_unknown_type_reports_and_control_flags()
    {
        var output = ExtractionParser.Parse("""
            {"events":[{"type":"glucose","value":6.4},{"type":"glucose","value":6.4},
                       {"type":"unknown"},{"type":"unknown"}],
             "unclear":[{"fragment":"synthetic unclear value","reason":"unit"}],
             "is_question":true,"undo":true,"needs_reply":true}
            """).ShouldNotBeNull();
        output.Events.ShouldHaveSingleItem().Intent.ShouldBe("unsure");
        output.Unclear.Select(e => e.Reason).ShouldBe(new[] { "unit", "type", "type" });
        output.Unclear[0].Fragment.ShouldBe("synthetic unclear value");
        output.IsQuestion.ShouldBeTrue(); output.Undo.ShouldBeTrue(); output.NeedsReply.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",\"needs_reply\":null", false)]
    [InlineData(",\"needs_reply\":false", false)]
    [InlineData(",\"needs_reply\":true", true)]
    public void Needs_reply_is_independent_of_is_question_and_defaults_false(string field, bool expected)
    {
        foreach (var isQuestion in new[] { "true", "false" })
        {
            var result = ExtractionParser.Parse("{\"events\":[],\"is_question\":" + isQuestion + field + "}");
            result!.NeedsReply.ShouldBe(expected);
            result.IsQuestion.ShouldBe(isQuestion == "true");
        }
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Wrong_type_needs_reply_invalidates_interpretation(string value) =>
        ExtractionParser.Parse("{\"events\":[],\"needs_reply\":" + value + "}").ShouldBeNull();
    [Fact]
    public void Parses_note_text_tags_and_intent()
    {
        var output = ExtractionParser.Parse("""{"events":[{"type":"NOTE","text":"short observation","tags":["Walk","Energy"],"intent":"record"}],"is_question":false}""");
        var note = output!.Events.ShouldHaveSingleItem();
        note.Type.ShouldBe("note");
        note.Text.ShouldBe("short observation");
        note.Tags.ShouldBe(new[] { "Walk", "Energy" });
        note.Intent.ShouldBe("record");
    }

    [Fact]
    public void Parses_events_unclear_and_is_question()
    {
        var json = """
            {"events":[{"type":"glucose","day":0,"time":"13:40","value":7.8,"unit":"mmol/L","context":"after_meal_1h"},{"type":"meal","day":0,"time":null,"meal_kind":"lunch","description":"гречка"}],"unclear":[{"fragment":"18","reason":"unit"}],"is_question":true}
            """;

        var output = ExtractionParser.Parse(json);

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(2);
        output.Events[0].Type.ShouldBe("glucose");
        output.Events[0].Day.ShouldBe(0);
        output.Events[0].Time.ShouldBe("13:40");
        output.Events[0].Value.ShouldBe(7.8m);
        output.Events[0].Unit.ShouldBe("mmol/L");
        output.Events[0].Context.ShouldBe("after_meal_1h");
        output.Events[1].Type.ShouldBe("meal");
        output.Events[1].MealKind.ShouldBe("lunch");
        output.Events[1].Description.ShouldBe("гречка");
        output.Unclear.Count.ShouldBe(1);
        output.Unclear[0].Fragment.ShouldBe("18");
        output.Unclear[0].Reason.ShouldBe("unit");
        output.IsQuestion.ShouldBeTrue();
    }

    [Fact]
    public void Strips_a_json_fence_and_prose_around_it()
    {
        var text = "Вот результат:\n```json\n{\"events\":[],\"unclear\":[],\"is_question\":false}\n```\nГотово.";

        var output = ExtractionParser.Parse(text);

        output.ShouldNotBeNull();
        output.Events.ShouldBeEmpty();
        output.Unclear.ShouldBeEmpty();
        output.IsQuestion.ShouldBeFalse();
    }

    [Fact]
    public void Accepts_comma_decimals_and_numbers_in_strings()
    {
        var output = ExtractionParser.Parse("""{"events":[{"type":"weight","day":"-1","kg":"64,5"},{"type":"glucose","value":"7.8"}]}""");

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(2);
        output.Events[0].Kg.ShouldBe(64.5m);
        output.Events[0].Day.ShouldBe(-1);
        output.Events[1].Value.ShouldBe(7.8m);
        output.Unclear.ShouldBeEmpty();
        output.IsQuestion.ShouldBeFalse();
    }

    [Fact]
    public void A_number_string_that_does_not_parse_becomes_null()
    {
        var output = ExtractionParser.Parse("""{"events":[{"type":"glucose","value":"seven"}]}""");

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(1);
        output.Events[0].Value.ShouldBeNull();
    }

    [Fact]
    public void Unknown_types_become_unclear_and_unknown_fields_are_dropped()
    {
        var output = ExtractionParser.Parse("""{"events":[{"type":"lab","value":1},{"type":" Glucose ","value":5.6,"mood":"good"}],"extra":1}""");

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(1);
        output.Events[0].Type.ShouldBe("glucose");
        output.Events[0].Value.ShouldBe(5.6m);
        var unclear = output.Unclear.ShouldHaveSingleItem();
        unclear.Reason.ShouldBe("type");
        unclear.Fragment.ShouldBeNull();
    }

    [Fact]
    public void Field_names_ignore_case()
    {
        var output = ExtractionParser.Parse("""{"Events":[{"Type":"weight","KG":60}],"IS_QUESTION":true}""");

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(1);
        output.Events[0].Kg.ShouldBe(60m);
        output.IsQuestion.ShouldBeTrue();
    }

    [Fact]
    public void Missing_lists_are_empty()
    {
        var output = ExtractionParser.Parse("{}");

        output.ShouldNotBeNull();
        output.Events.ShouldBeEmpty();
        output.Unclear.ShouldBeEmpty();
        output.IsQuestion.ShouldBeFalse();
    }

    [Fact]
    public void Null_entries_are_skipped()
    {
        var output = ExtractionParser.Parse("""{"events":[null,{"type":"weight","kg":60}],"unclear":[null]}""");

        output.ShouldNotBeNull();
        output.Events.Count.ShouldBe(1);
        output.Unclear.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here")]
    [InlineData("{not json}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("""{"events":"many"}""")]
    [InlineData("""{"events":[{"type":"glucose","time":1340}]}""")]
    [InlineData("""{"events":[],"is_question":"yes"}""")]
    public void Returns_null_for_anything_else(string text)
    {
        ExtractionParser.Parse(text).ShouldBeNull();
    }

    [Fact]
    public void Reads_each_events_intent()
    {
        var output = ExtractionParser.Parse(
            """{"events":[{"type":"glucose","value":5.0,"intent":"record"},{"type":"glucose","value":9.0,"intent":" Question_Only "},{"type":"weight","kg":70,"intent":"UNSURE"}],"unclear":[],"is_question":true}""");

        output.ShouldNotBeNull();
        output.Events.Select(e => e.Intent).ShouldBe(new[] { "record", "question_only", "unsure" });
    }

    [Theory]
    [InlineData(true, "unsure")]
    [InlineData(false, "record")]
    public void A_missing_or_unknown_intent_follows_is_question(bool isQuestion, string expected)
    {
        var flag = isQuestion ? "true" : "false";

        var output = ExtractionParser.Parse(
            $$"""{"events":[{"type":"glucose","value":5.0},{"type":"glucose","value":6.0,"intent":"maybe"}],"unclear":[],"is_question":{{flag}}}""");

        output.ShouldNotBeNull();
        output.Events.Select(e => e.Intent).ShouldBe(new[] { expected, expected });
    }

    [Fact]
    public void Reads_undo_and_defaults_it_to_false()
    {
        ExtractionParser.Parse("""{"events":[],"unclear":[],"is_question":false,"undo":true}""")!.Undo.ShouldBeTrue();
        ExtractionParser.Parse("""{"events":[],"unclear":[],"is_question":false}""")!.Undo.ShouldBeFalse();
    }
}
