using Assistant.Application.Telegram;

namespace Assistant.Application.Health.Documents;

public sealed record DocumentTextExtraction(string? Text, string? FailureReason, bool Truncated, bool PdfTextOnly);

public interface IDocumentTextExtractor
{
    /// <summary>Consumes bounded bytes from the current position; never owns or closes the caller stream.
    /// Cancellation is checked during copying/decoding and between synchronous PDF page parses.</summary>
    DocumentTextExtraction Extract(Stream bytes, DocumentAttachment metadata, CancellationToken cancellationToken);
}
