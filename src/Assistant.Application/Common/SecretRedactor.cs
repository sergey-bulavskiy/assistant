namespace Assistant.Application.Common;

public static class SecretRedactor
{
    public static string Redact(string text, string secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***");
}
