using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class BotOptionsValidatorTests
{
    private readonly BotOptionsValidator _validator = new();

    [Fact]
    public void Fails_when_token_missing()
    {
        var options = new BotOptions { Token = "", AllowedUserIdsRaw = "111,222" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("TELEGRAM_BOT_TOKEN"));
    }

    [Fact]
    public void Fails_when_allowed_user_ids_missing()
    {
        var options = new BotOptions { Token = "test-token", AllowedUserIdsRaw = "" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("ALLOWED_USER_IDS"));
    }

    [Fact]
    public void Fails_when_allowed_user_ids_not_numeric()
    {
        var options = new BotOptions { Token = "test-token", AllowedUserIdsRaw = "abc" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void Succeeds_with_valid_configuration()
    {
        var options = new BotOptions { Token = "test-token", AllowedUserIdsRaw = "111,222" };
        var result = _validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Failure_messages_never_contain_the_token_value()
    {
        var options = new BotOptions { Token = "super-secret-token-value", AllowedUserIdsRaw = "" };
        var result = _validator.Validate(null, options);
        result.Failures!.ShouldAllBe(f => !f.Contains("super-secret-token-value"));
    }
}
