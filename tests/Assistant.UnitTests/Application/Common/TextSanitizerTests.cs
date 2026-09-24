using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class TextSanitizerTests
{
    [Fact]
    public void Removes_nul_characters_from_text()
    {
        TextSanitizer.SanitizeText("hello\0world").ShouldBe("helloworld");
    }

    [Fact]
    public void Returns_null_for_null_text()
    {
        TextSanitizer.SanitizeText(null).ShouldBeNull();
    }

    [Fact]
    public void Removes_unicode_nul_escape_from_raw_json()
    {
        var raw = "{\"text\":\"hello\\u0000world\"}";
        TextSanitizer.SanitizeRawJson(raw).ShouldBe("{\"text\":\"helloworld\"}");
    }
}
