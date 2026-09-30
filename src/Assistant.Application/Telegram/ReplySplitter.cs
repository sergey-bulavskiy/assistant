namespace Assistant.Application.Telegram;

/// <summary>Splits a reply longer than Telegram's 4096-char message limit into several messages, at
/// paragraph, then line, then space boundaries when one exists inside the current window, otherwise
/// a hard cut (spec 2.2, §8.12). Replies are plain text, no parse mode -- a hard cut can never split
/// a formatting marker in half because there is none to split. Every split point is also checked
/// against .NET's UTF-16 surrogate pairs (spec §8.12: "never split inside a surrogate pair") --
/// relevant for any character outside the Basic Multilingual Plane (many emoji), which .NET stores
/// as two `char`s.</summary>
public static class ReplySplitter
{
    public const int TelegramMaxMessageLength = 4096;

    public static IReadOnlyList<string> Split(string text, int maxLength = TelegramMaxMessageLength)
    {
        if (text.Length <= maxLength)
        {
            return new[] { text };
        }

        var chunks = new List<string>();
        var remaining = text;

        while (remaining.Length > maxLength)
        {
            var splitAt = FindSplitPoint(remaining, maxLength);
            chunks.Add(remaining[..splitAt].TrimEnd());
            remaining = remaining[splitAt..].TrimStart('\n', ' ');
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    private static int FindSplitPoint(string text, int maxLength)
    {
        var window = text[..maxLength];

        var paragraphBreak = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraphBreak > 0)
        {
            return AvoidSplittingASurrogatePair(text, paragraphBreak);
        }

        var lineBreak = window.LastIndexOf('\n');
        if (lineBreak > 0)
        {
            return AvoidSplittingASurrogatePair(text, lineBreak);
        }

        var spaceBreak = window.LastIndexOf(' ');
        if (spaceBreak > 0)
        {
            return AvoidSplittingASurrogatePair(text, spaceBreak);
        }

        return AvoidSplittingASurrogatePair(text, maxLength);
    }

    /// <summary>If <paramref name="splitAt"/> falls between a high surrogate and its following low
    /// surrogate, moves the split back by one so the pair stays whole in the first chunk, rather than
    /// producing two chunks each holding one unpaired (invalid) UTF-16 code unit.</summary>
    private static int AvoidSplittingASurrogatePair(string text, int splitAt) =>
        splitAt > 0 && splitAt < text.Length && char.IsLowSurrogate(text[splitAt]) && char.IsHighSurrogate(text[splitAt - 1])
            ? splitAt - 1
            : splitAt;
}
