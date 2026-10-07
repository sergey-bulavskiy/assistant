using System.Text;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Assistant.Infrastructure.Health.Documents;

public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    public const int MaxBytes = 20_000_000;
    public const int MaxCharacters = 200_000;

    public DocumentTextExtraction Extract(Stream bytes, DocumentAttachment metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(metadata.FileName ?? string.Empty);
        var pdf = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
        var text = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase);
        if (!pdf && !text) return Failure("unsupported_format");
        using var input = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = bytes.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxBytes + 1L - input.Length));
            if (count == 0) break;
            input.Write(buffer, 0, count);
            if (input.Length > MaxBytes) return Failure("too_large", pdf);
        }
        var data = input.GetBuffer().AsMemory(0, (int)input.Length);
        var signature = data.Span.StartsWith("%PDF-"u8);
        if (pdf) return signature ? ExtractPdf(data, cancellationToken) : Failure("invalid_pdf", true);
        if (signature || data.Span.StartsWith("PK\u0003\u0004"u8)) return Failure("binary_content");
        return ExtractUtf8(data, cancellationToken);
    }

    private static DocumentTextExtraction ExtractUtf8(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var builder = new StringBuilder(Math.Min(data.Length, MaxCharacters));
        var truncated = false;
        var previousCr = false;
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var offset = data.Span.StartsWith(new byte[] {239,187,191}) ? 3 : 0;
        var characters = new char[4096];
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Min(4096, data.Length - offset);
                decoder.Convert(data.Span.Slice(offset, length), characters, offset + length == data.Length,
                    out var consumed, out var written, out _);
                offset += consumed;
                foreach (var character in characters.AsSpan(0, written))
                {
                    if (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) return Failure("binary_content");
                    if (character == '\n' && previousCr) { previousCr = false; continue; }
                    previousCr = character == '\r';
                    var normalized = previousCr ? '\n' : character;
                    if (builder.Length < MaxCharacters) builder.Append(normalized); else truncated = true;
                }
            } while (offset < data.Length);
        }
        catch (DecoderFallbackException) { return Failure("invalid_encoding"); }
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) { builder.Length--; truncated = true; }
        var result = builder.ToString();
        return string.IsNullOrWhiteSpace(result) ? Failure("no_readable_text") : new(result, null, truncated, false);
    }

    private static DocumentTextExtraction ExtractPdf(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            // ParsingOptions defaults to PdfPig's internal NoOpLog; never bridge parser messages to application logs.
            using var document = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = false });
            if (document.IsEncrypted) return Failure("encrypted_pdf", true);
            var builder = new StringBuilder();
            var truncated = false;
            for (var page = 1; page <= document.NumberOfPages; page++)
            {
                token.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(document.GetPage(page));
                token.ThrowIfCancellationRequested();
                if (page > 1) Append("\n\n\f\n\n");
                Append(text);
            }
            if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) { builder.Length--; truncated = true; }
            var result = builder.ToString();
            return string.IsNullOrWhiteSpace(result) ? Failure("no_readable_text", true) : new(result, null, truncated, true);

            void Append(string text)
            {
                var retained = Math.Min(text.Length, MaxCharacters - builder.Length);
                builder.Append(text, 0, retained);
                truncated |= retained < text.Length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) { return Failure("encrypted_pdf", true); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Parser diagnostics can include document text; callers receive only a fixed category.
            return Failure("invalid_pdf", true);
        }
    }

    private static DocumentTextExtraction Failure(string reason, bool pdf = false) => new(null, reason, false, pdf);
}
