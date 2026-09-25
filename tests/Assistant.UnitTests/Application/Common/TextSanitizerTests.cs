using System.Text.Json;
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

    private static string RoundTrippedText(string userText)
    {
        var json = JsonSerializer.Serialize(new { text = userText });
        var sanitized = TextSanitizer.SanitizeRawJson(json);

        using var doc = JsonDocument.Parse(sanitized);
        return doc.RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public void Real_nul_character_is_stripped_and_json_stays_valid()
    {
        // A genuine NUL char is what System.Text.Json turns into a bare `\u0000` escape
        // (preceded by zero, i.e. an even number of, backslashes) — this must be removed.
        var userText = "hello\0world";

        RoundTrippedText(userText).ShouldBe("helloworld");
    }

    [Fact]
    public void Literal_backslash_u0000_text_survives_untouched()
    {
        // Six literal characters typed by a user: \ u 0 0 0 0 (one backslash, not an escape).
        // JsonSerializer escapes the lone backslash, so the `\u0000` in the JSON output is
        // preceded by one (odd) backslash from that escaping — it must NOT be removed.
        var userText = "note \\u0000 here";

        RoundTrippedText(userText).ShouldBe(userText);
    }

    [Fact]
    public void Literal_double_backslash_then_u0000_survives_untouched()
    {
        // Two literal backslashes followed by literal "u0000" — an even backslash count, so
        // JsonSerializer's escaping produces a `\u0000`-looking run preceded by an even number
        // of backslashes that is still not a genuine NUL escape; must be left alone.
        var userText = "note \\\\u0000 here";

        RoundTrippedText(userText).ShouldBe(userText);
    }

    [Fact]
    public void Literal_backslash_followed_by_real_nul_removes_only_the_nul()
    {
        // One literal backslash immediately followed by a real NUL character. JsonSerializer
        // escapes the backslash (even pair) and then emits the genuine `\u0000` NUL escape right
        // after it — only the NUL escape must be removed, the literal backslash must survive.
        var userText = "note \\\u0000 here";

        RoundTrippedText(userText).ShouldBe("note \\ here");
    }
}
