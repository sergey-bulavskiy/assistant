using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Application.Telegram;

public class CommandParserTests
{
    [Theory]
    [InlineData("/version", "test_bot", "version")]
    [InlineData("/VERSION", "test_bot", "version")]
    [InlineData("/version@test_bot", "test_bot", "version")]
    [InlineData("/version@TEST_BOT", "test_bot", "version")]
    [InlineData("/start", "test_bot", "start")]
    [InlineData("/version extra words", "test_bot", "version")]
    public void Parses_recognized_commands(string text, string botUsername, string expected)
    {
        CommandParser.Parse(text, botUsername).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/version@other_bot")]
    [InlineData("plain text")]
    [InlineData("/")]
    [InlineData("")]
    public void Returns_null_for_unrecognized_input(string text)
    {
        CommandParser.Parse(text, "test_bot").ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_null_text()
    {
        CommandParser.Parse(null, "test_bot").ShouldBeNull();
    }
}
