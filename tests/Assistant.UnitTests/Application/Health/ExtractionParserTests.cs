using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class ExtractionParserTests
{
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
}
