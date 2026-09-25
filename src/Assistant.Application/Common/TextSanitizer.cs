using System.Text.RegularExpressions;

namespace Assistant.Application.Common;

public static partial class TextSanitizer
{
    public static string? SanitizeText(string? text) => text?.Replace("\0", string.Empty);

    // System.Text.Json encodes a real NUL character as the 6-char escape `\u0000`. A user who
    // types the literal 6 characters `\u0000` as text gets the same 6 chars in the JSON output,
    // but with the leading backslash itself escaped (JSON always escapes backslashes), so it's
    // preceded by an even number of backslashes rather than appearing as a bare, unescaped `\`.
    // A plain string.Replace can't tell these apart and used to corrupt the literal case into
    // invalid JSON (a dangling backslash before the closing quote). Only remove a `\u0000` that
    // is not itself escaped, i.e. one preceded by an even number (including zero) of backslashes.
    public static string SanitizeRawJson(string rawJson) => NulEscapeRegex().Replace(rawJson, "$1");

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000")]
    private static partial Regex NulEscapeRegex();
}
