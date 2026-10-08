using Assistant.Application.Reminders;

namespace Assistant.UnitTests.Reminders;

public sealed class ReminderParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static ReminderParseResult Parse(string text, int offset = 0) =>
        ReminderParser.Parse(text, "test_bot", Now, new(offset));

    [Theory]
    [InlineData("/remind in 10m synthetic task", 10)]
    [InlineData("/remind in 2h synthetic task", 120)]
    [InlineData("/remind in 1d synthetic task", 1440)]
    [InlineData("НАПОМНИ через 10 минут synthetic task", 10)]
    [InlineData("напомни через 1 минуту synthetic task", 1)]
    [InlineData("напомни через 2 минуты synthetic task", 2)]
    [InlineData("напомни через 2 часа synthetic task", 120)]
    [InlineData("напомни через 1 час synthetic task", 60)]
    [InlineData("напомни через 5 часов synthetic task", 300)]
    [InlineData("напомни через 1 день synthetic task", 1440)]
    [InlineData("напомни через 2 дня synthetic task", 2880)]
    [InlineData("напомни через 5 дней synthetic task", 7200)]
    public void Relative_forms_preserve_task_and_exact_due(string text, int minutes)
    {
        var result = Parse(text, 180);
        result.Recognized.ShouldBeTrue();
        result.Kind.ShouldBe("create");
        result.Request.ShouldBe(new ReminderRequest("synthetic task", Now.AddMinutes(minutes), null, 180));
    }

    [Theory]
    [InlineData("/remind at 2026-01-03T14:30 synthetic task")]
    [InlineData("напомни 03.01.2026 в 14:30 synthetic task")]
    public void Absolute_forms_apply_configured_offset(string text)
    {
        var result = Parse(text, 180);
        result.Request.ShouldBe(new ReminderRequest("synthetic task",
            new DateTimeOffset(2026, 1, 3, 11, 30, 0, TimeSpan.Zero), null, 180));
        result.Kind.ShouldBe("create");
    }

    [Theory]
    [InlineData("/remind daily 14:30 synthetic task")]
    [InlineData("напоминай каждый день в 14:30 synthetic task")]
    public void Daily_forms_select_next_strictly_future_occurrence(string text)
    {
        Parse(text, 180).Request.ShouldBe(new ReminderRequest("synthetic task",
            new DateTimeOffset(2026, 1, 3, 11, 30, 0, TimeSpan.Zero), 870, 180));
    }

    [Fact]
    public void Text_case_unicode_and_interior_spaces_are_preserved()
    {
        Parse("/remind@test_bot in 10m  Invented ЖЁлтый  task  ").Request!.Text
            .ShouldBe("Invented ЖЁлтый  task");
    }

    [Theory]
    [InlineData("/reminders", "list")]
    [InlineData("/reminders@test_bot", "list")]
    [InlineData("/reminder_settings", "settings")]
    public void Read_commands_are_recognized_without_create(string text, string kind)
    {
        var result = Parse(text);
        result.Kind.ShouldBe(kind);
        result.Recognized.ShouldBeTrue();
        result.Request.ShouldBeNull();
    }

    [Theory]
    [InlineData("ordinary synthetic text")]
    [InlineData("напоминание synthetic task")]
    [InlineData("/remind@other_bot in 10m task")]
    [InlineData("/reminders@other_bot")]
    [InlineData("/unknown in 10m task")]
    public void Unrelated_or_other_bot_inputs_fall_through(string text)
    {
        Parse(text).ShouldBe(new ReminderParseResult(false, "none"));
    }

    [Theory]
    [InlineData("/remind")]
    [InlineData("/remind in 0m task")]
    [InlineData("/remind in ١٠m task")]
    [InlineData("/remind in 367d task")]
    [InlineData("/remind in -1m task")]
    [InlineData("/remind in 1w task")]
    [InlineData("/remind in 10m ")]
    [InlineData("/remind at 2026-01-02T12:00 task")]
    [InlineData("/remind at 2026-01-02T11:59 task")]
    [InlineData("/remind at 2026-02-30T12:00 task")]
    [InlineData("/remind at 2027-01-03T12:01 task")]
    [InlineData("/remind daily 24:00 task")]
    [InlineData("напомни завтра task")]
    [InlineData("напоминай еженедельно task")]
    [InlineData("/remind in 10m task\tpart")]
    public void Invalid_requests_return_help_without_schedule(string text)
    {
        var result = Parse(text);
        result.ShouldBe(new ReminderParseResult(true, "invalid", Error: ReminderParser.Help));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void Task_length_boundary_is_enforced(int length, bool valid)
    {
        var result = Parse("/remind in 1m " + new string('x', length));
        result.Kind.ShouldBe(valid ? "create" : "invalid");
        if (valid) result.Request!.Text.ShouldBe(new string('x', length));
        else result.Request.ShouldBeNull();
    }

    [Fact]
    public void Maximum_horizon_is_inclusive_and_minute_beyond_is_rejected()
    {
        Parse("/remind in 366d task").Request!.DueAt.ShouldBe(
            new DateTimeOffset(2027, 1, 3, 12, 0, 0, TimeSpan.Zero));
        Parse("/remind in 527041m task").Kind.ShouldBe("invalid");
        Parse("/remind at 2027-01-03T12:00 task").Request!.DueAt.ShouldBe(
            new DateTimeOffset(2027, 1, 3, 12, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("-12:00", -720)]
    [InlineData("+14:00", 840)]
    [InlineData("+03:01", 181)]
    [InlineData("-00:01", -1)]
    public void Settings_accept_fixed_offset_boundaries_and_minutes(string offset, int expected)
    {
        var result = Parse($"/reminder_settings {offset} 22:00 08:00");
        result.Kind.ShouldBe("set_settings");
        result.Preferences.ShouldBe(new ReminderPreferences(expected, 1320, 480));
        result.Request.ShouldBeNull();
    }

    [Theory]
    [InlineData("/reminder_settings -12:01 22:00 08:00")]
    [InlineData("/reminder_settings +14:01 22:00 08:00")]
    [InlineData("/reminder_settings +03:60 22:00 08:00")]
    [InlineData("/reminder_settings UTC 22:00 08:00")]
    [InlineData("/reminder_settings +٠٣:00 22:00 08:00")]
    [InlineData("/reminder_settings +03:00 22:00 22:00")]
    [InlineData("/reminder_settings +03:00 24:00 08:00")]
    [InlineData("/reminder_settings +03:00 22:00")]
    [InlineData("/reminder_settings +03:00 off")]
    public void Invalid_settings_return_help_and_no_preferences(string text)
    {
        Parse(text).ShouldBe(new ReminderParseResult(true, "invalid", Error: ReminderParser.Help));
    }

    [Theory]
    [InlineData("напомни task", true)]
    [InlineData(" НАПОМИНАЙ каждый день в 12:00 task", true)]
    [InlineData("напоминание task", false)]
    [InlineData("say напомни task", false)]
    public void Conversational_detection_requires_whole_leading_keyword(string text, bool expected) =>
        ReminderParser.LooksConversational(text).ShouldBe(expected);

    [Theory]
    [InlineData("/remind at 0001-01-01T00:00 task", 840)]
    [InlineData("/remind at 9999-12-31T23:59 task", -720)]
    public void Offset_conversion_overflow_is_rejected_with_help(string text,int offset)
    {
        Parse(text,offset).ShouldBe(new ReminderParseResult(true,"invalid",Error:ReminderParser.Help));
    }

    [Fact]
    public void Oversized_input_is_rejected_without_schedule()
    {
        Parse("/remind in 1m "+new string('x',2048)).ShouldBe(new ReminderParseResult(true,"invalid",Error:ReminderParser.Help));
    }
}
