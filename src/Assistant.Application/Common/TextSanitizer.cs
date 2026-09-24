namespace Assistant.Application.Common;

public static class TextSanitizer
{
    public static string? SanitizeText(string? text) => text?.Replace("\0", string.Empty);

    public static string SanitizeRawJson(string rawJson) => rawJson.Replace("\\u0000", string.Empty);
}
