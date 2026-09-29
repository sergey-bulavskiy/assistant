using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class BotOptionsValidatorTests
{
    private readonly BotOptionsValidator _validator = new();

    private const string ValidKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    [Fact]
    public void Fails_when_manager_token_missing()
    {
        var options = new BotOptions { ManagerToken = "", TokenEncryptionKey = ValidKey };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("TELEGRAM_MANAGER_BOT_TOKEN"));
    }

    [Fact]
    public void Fails_when_encryption_key_missing()
    {
        var options = new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = "" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("TOKEN_ENCRYPTION_KEY"));
    }

    [Fact]
    public void Fails_when_encryption_key_is_not_valid_base64()
    {
        var options = new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = "not-base64!!" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void Fails_when_encryption_key_is_not_32_bytes()
    {
        var options = new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = Convert.ToBase64String(new byte[16]) };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void Succeeds_with_valid_configuration()
    {
        var options = new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = ValidKey };
        var result = _validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Failure_messages_never_contain_the_token_value()
    {
        var options = new BotOptions { ManagerToken = "super-secret-token-value", TokenEncryptionKey = "" };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures!.ShouldAllBe(f => !f.Contains("super-secret-token-value"));
    }
}
