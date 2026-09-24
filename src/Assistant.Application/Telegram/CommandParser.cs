namespace Assistant.Application.Telegram;

public static class CommandParser
{
    public static string? Parse(string? text, string botUsername)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
        {
            return null;
        }

        var firstToken = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(firstToken) || firstToken.Length < 2)
        {
            return null;
        }

        var body = firstToken[1..];
        var atIndex = body.IndexOf('@');
        if (atIndex >= 0)
        {
            var mentioned = body[(atIndex + 1)..];
            if (!string.Equals(mentioned, botUsername, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            body = body[..atIndex];
        }

        return body.Length == 0 ? null : body.ToLowerInvariant();
    }
}
