using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.Application.Health.Documents;

public static class HealthDocumentCandidate
{
    public static bool IsValid(IncomingMessage message) => !message.IsEdit
        && message.Kind == MessageKind.Document && message.Document is { } document
        && !string.IsNullOrWhiteSpace(document.FileId) && document.FileId.Length <= 1024
        && (document.FileUniqueId is null || document.FileUniqueId.Length <= 1024)
        && message.ChatId != 0 && message.MessageId > 0
        && message.ChatType is "private" or "group" or "supergroup"
        && (message.Text is null || message.Text.Length <= 4096);

    public static bool IsSupported(DocumentAttachment attachment)
    {
        var extension = Path.GetExtension(attachment.FileName ?? "");
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attachment.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attachment.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Metadata(string? value)
    {
        if (value is null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 255 ? clean : clean[..254] + "…";
    }

    public static string? FileNameMetadata(string? value)
    {
        if (value is null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length <= 255) return clean;
        var extension = Path.GetExtension(clean);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            return clean[..(254 - extension.Length)] + "…" + extension;
        return clean[..254] + "…";
    }
}

public sealed class HealthDocumentIntakePersistenceException : Exception
{
    public HealthDocumentIntakePersistenceException() : base("Health document intake persistence failed.") { }
    public static bool IsRetryable(Exception exception) =>
        exception is System.Data.Common.DbException { IsTransient: true } or IOException or TimeoutException
        || exception.InnerException is { } inner && IsRetryable(inner);
}
