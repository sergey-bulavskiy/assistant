using Assistant.Application.Expectations;

namespace Assistant.UnitTests.Expectations;

public sealed class ExpectationParserTests
{
    private const string Id = "10000000000000000000000000000001";

    [Theory]
    [InlineData("health", "glucose")]
    [InlineData("health", "insulin")]
    [InlineData("health", "meal")]
    [InlineData("health", "symptom")]
    [InlineData("health", "weight")]
    [InlineData("health", "blood_pressure")]
    [InlineData("vet", "glucose")]
    [InlineData("vet", "insulin")]
    public void Supported_type_parses_exact_daily_deadline_and_required_grace(string role, string type)
    {
        ExpectationParser.Parse($"/expect {type} daily 09:00 grace 30", "synthetic_bot", role)
            .ShouldBe(new ExpectationCommand("create", EventType: type, DeadlineMinute: 540, GraceMinutes: 30));
    }

    [Theory]
    [InlineData("health", "note")]
    [InlineData("vet", "meal")]
    [InlineData("vet", "weight")]
    [InlineData("health", "unknown")]
    [InlineData("health", "Glucose")]
    [InlineData("general", "glucose")]
    public void Unsupported_role_or_type_returns_help_without_creation(string role, string type)
    {
        ExpectationParser.Parse($"/expect {type} daily 09:00 grace 30", "synthetic_bot", role)
            .ShouldBe(new ExpectationCommand("invalid", Error: ExpectationParser.Help));
    }

    [Theory]
    [InlineData("/expect")]
    [InlineData("/expect glucose")]
    [InlineData("/expect glucose daily 09:00")]
    [InlineData("/expect glucose daily 09:00 grace")]
    [InlineData("/expect glucose weekly 09:00 grace 30")]
    [InlineData("/expect glucose daily 09:00 10:00 grace 30")]
    [InlineData("/expect glucose daily 9:00 grace 30")]
    [InlineData("/expect glucose daily 24:00 grace 0")]
    [InlineData("/expect glucose daily 09:60 grace 0")]
    [InlineData("/expect glucose daily 09:00 grace -1")]
    [InlineData("/expect glucose daily 09:00 grace +1")]
    [InlineData("/expect glucose daily 09:00 grace 181")]
    [InlineData("/expect glucose daily 09:00 grace 1.5")]
    [InlineData("/expect glucose daily 09:00 grace thirty")]
    [InlineData("/expect glucose daily 09:00 grace 30 synthetic-subject")]
    [InlineData("/expect glucose daily 09:00 Grace 30")]
    public void Invalid_creation_arguments_return_example(string text)
    {
        ExpectationParser.Parse(text, "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand("invalid", Error: ExpectationParser.Help));
    }

    [Theory]
    [InlineData("09:00", 0, 540)]
    [InlineData("09:00", 180, 540)]
    [InlineData("23:30", 29, 1410)]
    [InlineData("23:59", 0, 1439)]
    [InlineData("00:00", 0, 0)]
    public void Grace_boundaries_and_last_valid_minute_are_preserved(string time, int grace, int minute)
    {
        ExpectationParser.Parse($"/expect glucose daily {time} grace {grace}", "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand("create", EventType: "glucose", DeadlineMinute: minute, GraceMinutes: grace));
    }

    [Theory]
    [InlineData("23:30", 30)]
    [InlineData("23:59", 1)]
    [InlineData("23:00", 61)]
    public void Deadline_plus_grace_cannot_reach_next_midnight(string time, int grace)
    {
        ExpectationParser.Parse($"/expect glucose daily {time} grace {grace}", "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand("invalid", Error: ExpectationParser.Help));
    }

    [Fact]
    public void Edit_preserves_id_and_contains_no_new_subject_or_type()
    {
        ExpectationParser.Parse($"/expect_edit {Id} daily 10:15 grace 5", "synthetic_bot", "vet")
            .ShouldBe(new ExpectationCommand("edit", Guid.ParseExact(Id, "N"), DeadlineMinute: 615, GraceMinutes: 5));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public void Management_command_parses_exact_stable_id(string verb)
    {
        ExpectationParser.Parse($"/expect_{verb} {Id}", "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand(verb, Guid.ParseExact(Id, "N")));
    }

    [Theory]
    [InlineData("/expect_pause")]
    [InlineData("/expect_resume not-an-id")]
    [InlineData("/expect_cancel 10000000-0000-0000-0000-000000000001")]
    [InlineData("/expect_pause 10000000000000000000000000000001 extra")]
    [InlineData("/expect_edit 10000000000000000000000000000001 daily 09:00")]
    [InlineData("/expect_edit 10000000000000000000000000000001 daily 09:00 grace 30 offset +03:00")]
    [InlineData("/expectations extra")]
    public void Management_rejects_missing_wrong_or_extra_arguments(string text)
    {
        ExpectationParser.Parse(text, "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand("invalid", Error: ExpectationParser.Help));
    }

    [Fact]
    public void List_has_no_subject_or_schedule_arguments()
    {
        ExpectationParser.Parse("/expectations", "synthetic_bot", "health").ShouldBe(new ExpectationCommand("list"));
    }

    [Fact]
    public void Addressed_command_and_whitespace_preserve_payload()
    {
        ExpectationParser.Parse("/EXPECT@SYNTHETIC_BOT\tglucose  daily 09:00\tgrace 30", "synthetic_bot", "health")
            .ShouldBe(new ExpectationCommand("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30));
    }

    [Theory]
    [InlineData("/expect@another_bot glucose daily 09:00 grace 30")]
    [InlineData("/remind daily 09:00 synthetic task")]
    [InlineData("check glucose daily at 09:00")]
    [InlineData("ordinary synthetic text")]
    [InlineData("")]
    public void Unaddressed_or_unrecognized_text_is_not_an_expectation_command(string text)
    {
        ExpectationParser.Parse(text, "synthetic_bot", "health").ShouldBeNull();
    }
}
