using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class HealthEventTextTests
{
    private static HealthEventInfo Info(long id, string type, string json, string at = "2030-02-07T08:30:00Z") =>
        new(id, type, DateTimeOffset.Parse(at), json, null);

    [Theory]
    [InlineData("glucose", "{\"value\":7.8,\"context\":\"after_meal_1h\"}", "глюкоза 7.8 ммоль/л (через 1 ч после еды)")]
    [InlineData("glucose", "{\"value\":5,\"context\":\"fasting\"}", "глюкоза 5.0 ммоль/л (натощак)")]
    [InlineData("glucose", "{\"value\":6.1,\"context\":\"other\"}", "глюкоза 6.1 ммоль/л")]
    [InlineData("insulin", "{\"kind\":\"short\",\"name\":null,\"units\":6}", "инсулин 6 ед., короткий")]
    [InlineData("insulin", "{\"kind\":\"unknown\",\"name\":\"ExampleName\",\"units\":7.5}", "инсулин 7.5 ед., ExampleName")]
    [InlineData("meal", "{\"meal_kind\":\"lunch\",\"description\":\"гречка\"}", "обед: гречка")]
    [InlineData("meal", "{\"meal_kind\":\"other\",\"description\":\"яблоко\"}", "еда: яблоко")]
    [InlineData("symptom", "{\"code\":\"headache\",\"text\":\"болит голова\"}", "симптом: болит голова")]
    [InlineData("symptom", "{\"code\":\"headache\",\"text\":\"\"}", "симптом: headache")]
    [InlineData("weight", "{\"kg\":64.5}", "вес 64.5 кг")]
    [InlineData("blood_pressure", "{\"systolic\":128,\"diastolic\":84,\"pulse\":76}", "давление 128/84, пульс 76")]
    [InlineData("blood_pressure", "{\"systolic\":128,\"diastolic\":84,\"pulse\":null}", "давление 128/84")]
    [InlineData("lab", "{}", "lab")]
    [InlineData("weight", "not json", "weight")]
    public void Describe_shows_the_stored_values(string type, string json, string expected)
    {
        HealthEventText.Describe(Info(1, type, json)).ShouldBe(expected);
    }

    [Fact]
    public void Line_shows_the_id_and_local_time()
    {
        var info = Info(12, "glucose", "{\"value\":7.8,\"context\":\"after_meal_1h\"}");

        HealthEventText.Line(info, ProfileTimeZone.Find("Europe/Berlin")).ShouldBe("#12 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)");
    }
}
