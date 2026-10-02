using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class ExtractionRepliesTests
{
    [Fact]
    public void Failure_notice_is_the_fixed_text()
    {
        ExtractionReplies.FailureNotice.ShouldBe(
            "⚠️ Не смог обработать сообщение — ничего не записано. Повторите позже. Если значение опасное, не ждите бота: свяжитесь с врачом.");
    }

    [Theory]
    [InlineData("18", "unit", "утром 18", "Не понял «18» — уточните единицы (нужно в ммоль/л).")]
    [InlineData("7.8", "value", "сахар 7.8 после еды", "Не понял «7.8» — уточните значение.")]
    [InlineData("в 25:00", "time", "замер в 25:00", "Не понял «в 25:00» — уточните время.")]
    [InlineData("18", "type", "утром 18", "Не понял «18» — уточните что это за показатель.")]
    [InlineData("18", "smell", "утром 18", "Не понял «18» — уточните значение.")]
    public void Clarification_names_the_fragment_and_what_to_clarify(string fragment, string reason, string message, string expected)
    {
        var problem = new ExtractedUnclear { Fragment = fragment, Reason = reason };

        ExtractionReplies.Clarification(problem, message).ShouldBe(expected);
    }

    [Fact]
    public void A_decimal_fragment_is_shown_as_the_user_wrote_it()
    {
        var problem = new ExtractedUnclear { Fragment = "7.8", Reason = "value" };

        ExtractionReplies.Clarification(problem, "сахар 7,8").ShouldBe("Не понял «7,8» — уточните значение.");
    }

    [Theory]
    [InlineData("25", "unit", "утром 18")]
    [InlineData(null, "unit", "утром 18")]
    [InlineData("", "value", "утром 18")]
    public void Generic_reply_when_the_fragment_is_not_literal(string? fragment, string reason, string message)
    {
        var problem = new ExtractedUnclear { Fragment = fragment, Reason = reason };

        ExtractionReplies.Clarification(problem, message).ShouldBe("Не понял одно из значений — уточните, пожалуйста.");
    }

    [Fact]
    public void Generic_reply_when_the_fragment_is_too_long()
    {
        var fragment = new string('x', 31);
        var problem = new ExtractedUnclear { Fragment = fragment, Reason = "value" };

        ExtractionReplies.Clarification(problem, fragment).ShouldBe("Не понял одно из значений — уточните, пожалуйста.");
    }
}
