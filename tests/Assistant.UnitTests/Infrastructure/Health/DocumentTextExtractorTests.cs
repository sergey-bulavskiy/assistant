using System.Text;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Health.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Assistant.UnitTests.Infrastructure.Health;

public sealed class DocumentTextExtractorTests
{
    private readonly DocumentTextExtractor _extractor = new();
    private static DocumentAttachment Metadata(string? name = "synthetic.txt", string? mime = null, long? size = null)
        => new("synthetic-file", null, name, mime, size);

    [Theory]
    [InlineData("SYNTHETIC.MD", null)]
    [InlineData(null, "text/plain")]
    public void StrictUtf8BomAndNewlinesProduceReadableText(string? name, string? mime)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("synthetic\r\nsecond\rthird\n")).ToArray());
        var result = _extractor.Extract(bytes, Metadata(name, mime), default);
        result.Text.ShouldBe("synthetic\nsecond\nthird\n");
        result.FailureReason.ShouldBeNull();
        result.Truncated.ShouldBeFalse();
        result.PdfTextOnly.ShouldBeFalse();
        bytes.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(new byte[] {0xff,0xfe}, "invalid_encoding")]
    [InlineData(new byte[] {65,0,66}, "binary_content")]
    [InlineData(new byte[] {80,75,3,4,65}, "binary_content")]
    public void InvalidEncodingAndRenamedBinaryStayMetadataOnly(byte[] payload, string failure)
    {
        using var bytes = new MemoryStream(payload);
        var result = _extractor.Extract(bytes, Metadata(), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe(failure);
        bytes.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void TextCapIsExactAndInvalidTailCannotBecomeAReadablePrefix()
    {
        using var exact = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 200_000)));
        var result = _extractor.Extract(exact, Metadata(), default);
        result.Text.ShouldBe(new string('a', 200_000));
        result.Truncated.ShouldBeFalse();
        using var longer = new MemoryStream(Encoding.UTF8.GetBytes(new string('b', 200_001)));
        var capped = _extractor.Extract(longer, Metadata(), default);
        capped.Text.ShouldBe(new string('b', 200_000));
        capped.Truncated.ShouldBeTrue();
        using var invalidTail = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 200_001)).Concat(new byte[] {0xff}).ToArray());
        _extractor.Extract(invalidTail, Metadata(), default).FailureReason.ShouldBe("invalid_encoding");
    }

    [Fact]
    public void ByteCapUsesActualLengthEvenWhenMetadataUnderstatesIt()
    {
        using var bytes = new MemoryStream(new byte[20_000_001]);
        var result = _extractor.Extract(bytes, Metadata(size: 1), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe("too_large");
        bytes.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("synthetic.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void MissingOrUnsupportedHintsDoNotDecodeArbitraryBytes(string? name, string? mime)
    {
        using var bytes = new MemoryStream("synthetic"u8.ToArray());
        _extractor.Extract(bytes, Metadata(name, mime), default).FailureReason.ShouldBe("unsupported_format");
        bytes.Position.ShouldBe(0);
    }

    [Fact]
    public void WhitespaceAndEmptyTextHaveNoReadableLayer()
    {
        foreach (var text in new[] { "", " \r\n\t" })
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var result = _extractor.Extract(bytes, Metadata(), default);
            result.Text.ShouldBeNull();
            result.FailureReason.ShouldBe("no_readable_text");
        }
    }

    private static byte[] Pdf(params string[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(400, 300);
            if (text.Length > 0) page.AddText(text, 12, new PdfPoint(20, 250), font);
        }
        return builder.Build();
    }

    [Fact]
    public void PdfTextIsReadInPageOrderWithBoundariesAndCallerStreamRemainsOpen()
    {
        using var bytes = new MemoryStream(Pdf("synthetic first", "synthetic second"));
        var result = _extractor.Extract(bytes, Metadata("SYNTHETIC.PDF"), default);
        result.Text.ShouldBe("synthetic first\n\n\f\n\nsynthetic second");
        result.PdfTextOnly.ShouldBeTrue();
        result.FailureReason.ShouldBeNull();
        result.Truncated.ShouldBeFalse();
        bytes.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void NoTextPdfDoesNotClaimImageOrScanReading()
    {
        using var bytes = new MemoryStream(Pdf(""));
        var result = _extractor.Extract(bytes, Metadata(null, "application/pdf"), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe("no_readable_text");
        result.PdfTextOnly.ShouldBeTrue();
    }

    [Theory]
    [InlineData("%PDF-1.7\nsynthetic corrupt")]
    [InlineData("synthetic fake pdf")]
    public void CorruptAndFalseSignaturePdfReturnFixedFailure(string content)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = _extractor.Extract(bytes, Metadata("synthetic.pdf"), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe("invalid_pdf");
        bytes.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void PdfRenamedAsTextIsNotDecodedAsBinary()
    {
        using var bytes = new MemoryStream(Pdf("synthetic layer"));
        var result = _extractor.Extract(bytes, Metadata(), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe("binary_content");
    }

    [Fact]
    public void CancelledInputDoesNotReadOrCloseCallerStream()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var bytes = new MemoryStream(Pdf("synthetic"));
        Should.Throw<OperationCanceledException>(() => _extractor.Extract(bytes, Metadata("synthetic.pdf"), cancelled.Token));
        bytes.Position.ShouldBe(0);
        bytes.CanRead.ShouldBeTrue();
    }
}
