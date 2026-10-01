using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Infrastructure.Llm;

/// <summary>Shared retry-time parsing for both provider factories (spec §10.5/§10.6): a provider's
/// 429 may carry the retry time in several shapes, checked in this order, first match wins:
/// `Retry-After` as either an integer number of seconds or an HTTP-date; `retry-after-ms`
/// (milliseconds, seen on some gateways); OpenAI's `x-ratelimit-reset-requests`/`-tokens` (a Go-style
/// duration string like `6m0s` or `20ms`); Anthropic's `anthropic-ratelimit-requests-reset`/
/// `-tokens-reset` (an RFC 3339 timestamp). Returns null (cooldown fallback) when none parse.</summary>
public static class RetryTimeParser
{
    private static readonly string[] ResetHeaderNames =
    {
        "anthropic-ratelimit-requests-reset",
        "anthropic-ratelimit-tokens-reset",
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens"
    };

    private static readonly Regex DurationTokenRegex = new(@"(\d+(?:\.\d+)?)(ms|ns|us|µs|s|m|h)", RegexOptions.Compiled);

    public static DateTimeOffset? Parse(Func<string, string?> getHeader, DateTimeOffset now)
    {
        var retryAfter = getHeader("Retry-After");
        if (!string.IsNullOrWhiteSpace(retryAfter))
        {
            if (int.TryParse(retryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return now.AddSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }
        }

        var retryAfterMs = getHeader("retry-after-ms");
        if (!string.IsNullOrWhiteSpace(retryAfterMs) &&
            double.TryParse(retryAfterMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return now.AddMilliseconds(ms);
        }

        foreach (var name in ResetHeaderNames)
        {
            var value = getHeader(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reset))
            {
                return reset;
            }

            if (TryParseDuration(value, out var duration))
            {
                return now.Add(duration);
            }
        }

        return null;
    }

    private static bool TryParseDuration(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var matches = DurationTokenRegex.Matches(value);
        if (matches.Count == 0)
        {
            return false;
        }

        var totalSeconds = 0d;
        foreach (Match match in matches)
        {
            var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            totalSeconds += match.Groups[2].Value switch
            {
                "h" => amount * 3600,
                "m" => amount * 60,
                "s" => amount,
                "ms" => amount / 1_000,
                "us" or "µs" => amount / 1_000_000,
                "ns" => amount / 1_000_000_000,
                _ => 0
            };
        }

        result = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }
}
