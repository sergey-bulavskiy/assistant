using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Application.Telegram;

public class CommandParserArgsTests
{
    [Fact]
    public void Returns_the_text_after_the_command_word()
    {
        CommandParser.ParseArgs("/newbot general").ShouldBe("general");
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        CommandParser.ParseArgs("/newbot   general  ").ShouldBe("general");
    }

    [Fact]
    public void Returns_null_when_there_is_no_argument()
    {
        CommandParser.ParseArgs("/settings").ShouldBeNull();
    }

    [Fact]
    public void Returns_null_when_the_argument_is_only_whitespace()
    {
        CommandParser.ParseArgs("/newbot    ").ShouldBeNull();
    }
}
