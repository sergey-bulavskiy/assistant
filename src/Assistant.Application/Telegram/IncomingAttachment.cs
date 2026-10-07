namespace Assistant.Application.Telegram;

/// <summary>Opaque identifiers belong to the receiving bot; names are metadata, never paths.</summary>
public sealed record DocumentAttachment(
    string FileId, string? FileUniqueId, string? FileName, string? MimeType, long? FileSize);

public sealed record PhotoSizeAttachment(
    string FileId, string? FileUniqueId, int Width, int Height, long? FileSize);

/// <summary>Resolution variants of one image, not separate source measurements.</summary>
public sealed record PhotoAttachment(IReadOnlyList<PhotoSizeAttachment> Sizes);

public enum TelegramFileDownloadFailure { TooLarge, Unavailable, Timeout }

/// <summary>Contains a fixed category only; never a token-bearing URL or provider exception.</summary>
public sealed class TelegramFileDownloadException(TelegramFileDownloadFailure reason)
    : IOException($"Telegram file download failed ({reason}).")
{
    public TelegramFileDownloadFailure Reason { get; } = reason;
}
