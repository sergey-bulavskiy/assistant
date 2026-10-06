using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class ExtractionPromptTests
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void System_prompt_is_the_instructions_plus_a_runtime_block()
    {
        var prompt = ExtractionPrompt.BuildSystemPrompt("test instructions\n", Now, Now.AddMinutes(-2), "Europe/Berlin");

        prompt.ShouldBe(
            "test instructions\n\n## Runtime\n\n" +
            "- Time zone: Europe/Berlin\n" +
            "- Current local date and time: 2030-02-07 11:00 (Thursday)\n" +
            "- The message was sent at local time: 2030-02-07 10:58 (Thursday)\n" +
            "- Event types: glucose, insulin, meal, symptom, weight, blood_pressure, note\n" +
            "- glucose.context: fasting, before_meal, after_meal_1h, after_meal_2h, bedtime, night, other\n" +
            "- glucose.unit: mmol/L, mg/dL\n" +
            "- insulin.kind: long, short, unknown\n" +
            "- meal.meal_kind: breakfast, lunch, dinner, snack, other\n" +
            "- symptom.code: headache, vision_disturbance, epigastric_pain, nausea_vomiting, swelling, bleeding, abdominal_pain, shortness_of_breath, seizure, dizziness, hypo_symptoms, fever, other\n" +
            "- unclear.reason: unit, value, time, type\n");
    }

    [Fact]
    public void Local_date_follows_the_zone()
    {
        var late = new DateTimeOffset(2030, 2, 7, 22, 30, 0, TimeSpan.Zero);

        var prompt = ExtractionPrompt.BuildSystemPrompt("x", late, late, "Asia/Tokyo");

        prompt.ShouldContain("- Current local date and time: 2030-02-08 07:30 (Friday)");
    }

    [Fact]
    public void Unknown_zone_is_shown_as_utc()
    {
        var prompt = ExtractionPrompt.BuildSystemPrompt("x", Now, Now, "Mars/Base");

        prompt.ShouldContain("- Time zone: UTC");
        prompt.ShouldContain("- Current local date and time: 2030-02-07 10:00 (Thursday)");
    }

    [Theory]
    [InlineData("сахар 5.6", true)]
    [InlineData("abc", true)]
    [InlineData("5.6", true)]
    [InlineData("ok", false)]
    [InlineData("  7  ", false)]
    [InlineData("👍👍", false)]
    [InlineData("!!!", false)]
    [InlineData("...?", false)]
    public void ShouldExtract_skips_short_and_emoji_only_text(string text, bool expected)
    {
        ExtractionPrompt.ShouldExtract(text).ShouldBe(expected);
    }
}
