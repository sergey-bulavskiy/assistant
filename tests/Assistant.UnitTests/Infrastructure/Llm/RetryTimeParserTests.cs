using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class RetryTimeParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Func<string, string?> HeaderMap(params (string Name, string Value)[] headers) =>
        name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Retry_After_as_seconds_wins_over_everything_else()
    {
        var result = RetryTimeParser.Parse(HeaderMap(("Retry-After", "30"), ("retry-after-ms", "999000")), Now);
        result.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public void Retry_After_as_an_HTTP_date_is_parsed()
    {
        var result = RetryTimeParser.Parse(HeaderMap(("Retry-After", "Thu, 01 Oct 2026 12:05:00 GMT")), Now);
        result.ShouldBe(new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Retry_after_ms_is_used_when_Retry_After_is_absent()
    {
        var result = RetryTimeParser.Parse(HeaderMap(("retry-after-ms", "1500")), Now);
        result.ShouldBe(Now.AddMilliseconds(1500));
    }

    [Fact]
    public void Anthropic_reset_header_as_RFC3339_is_used_as_a_last_resort()
    {
        var result = RetryTimeParser.Parse(HeaderMap(("anthropic-ratelimit-requests-reset", "2026-10-01T12:10:00Z")), Now);
        result.ShouldBe(new DateTimeOffset(2026, 10, 1, 12, 10, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("x-ratelimit-reset-requests", "6m0s", 360)]
    [InlineData("x-ratelimit-reset-tokens", "20ms", 0.02)]
    [InlineData("x-ratelimit-reset-requests", "1h2m3s", 3723)]
    public void OpenAI_style_duration_reset_headers_are_parsed(string header, string value, double expectedSeconds)
    {
        var result = RetryTimeParser.Parse(HeaderMap((header, value)), Now);
        result.ShouldBe(Now.AddSeconds(expectedSeconds));
    }

    [Fact]
    public void No_header_present_returns_null_for_the_cooldown_fallback()
    {
        RetryTimeParser.Parse(_ => null, Now).ShouldBeNull();
    }

    [Fact]
    public void An_unparseable_Retry_After_falls_back_to_reset_headers()
    {
        var result = RetryTimeParser.Parse(HeaderMap(("Retry-After", "not-a-value"), ("x-ratelimit-reset-requests", "5s")), Now);
        result.ShouldBe(Now.AddSeconds(5));
    }
}
