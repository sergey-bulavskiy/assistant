using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class SecretRedactorTests
{
    [Fact]
    public void Replaces_secret_occurrences_with_mask()
    {
        SecretRedactor.Redact("token 123:ABC-token in url", "123:ABC-token").ShouldBe("token *** in url");
    }

    [Fact]
    public void Returns_text_unchanged_when_secret_is_empty()
    {
        SecretRedactor.Redact("no secret here", "").ShouldBe("no secret here");
    }

    [Fact]
    public void Does_not_throw_when_secret_is_absent()
    {
        SecretRedactor.Redact("nothing to redact", "not-present").ShouldBe("nothing to redact");
    }
}
