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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageOnlyAndMixedPdfReportOnlyTheirReadableTextLayer(bool withText)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(400, 300);
        // A generated one-pixel blue PNG: invented bytes, not an uploaded scan.
        page.AddPng(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2Bg+P8fAAMCAf/Jsq3uAAAAAElFTkSuQmCC"),
            new PdfRectangle(20, 20, 100, 100));
        if (withText)
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText("synthetic layer", 12, new PdfPoint(20, 250), font);
        }
        using var bytes = new MemoryStream(builder.Build());
        var result = _extractor.Extract(bytes, Metadata("synthetic.pdf"), default);
        result.Text.ShouldBe(withText ? "synthetic layer" : null);
        result.FailureReason.ShouldBe(withText ? null : "no_readable_text");
        result.PdfTextOnly.ShouldBeTrue();
        result.Truncated.ShouldBeFalse();
        bytes.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void PasswordProtectedPdfReturnsNoTextWithoutPasswordSolicitation()
    {
        // Confirm the generated fixture has a real readable layer when opened with its invented password.
        using (var readable = UglyToad.PdfPig.PdfDocument.Open(Convert.FromBase64String(SyntheticEncryptedPdf),
            new UglyToad.PdfPig.ParsingOptions { Password = "synthetic-reader" }))
            readable.GetPage(1).Text.ShouldBe("synthetic encrypted layer");
        using var bytes = new MemoryStream(Convert.FromBase64String(SyntheticEncryptedPdf));
        var result = _extractor.Extract(bytes, Metadata("synthetic.pdf"), default);
        result.Text.ShouldBeNull();
        result.FailureReason.ShouldBe("encrypted_pdf");
        result.PdfTextOnly.ShouldBeTrue();
        result.Truncated.ShouldBeFalse();
        bytes.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void CancellationDuringCopyStopsBeforeNextReadAndLeavesStreamOpen()
    {
        using var cancelled = new CancellationTokenSource();
        using var bytes = new CancellingStream(new byte[16_384], cancelled);
        Should.Throw<OperationCanceledException>(() => _extractor.Extract(bytes, Metadata(), cancelled.Token));
        bytes.Reads.ShouldBe(1);
        bytes.Position.ShouldBe(8192);
        bytes.CanRead.ShouldBeTrue();
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            var result = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return result;
        }
    }

    // Generated from six invented PDF objects, standard R2 encryption and the invented
    // password "synthetic-reader"; its decrypted layer is "synthetic encrypted layer".
    // This fixed fixture avoids bringing a PDF encryption implementation into the tests.
    private const string SyntheticEncryptedPdf =
        "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUg" +
        "L1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAg" +
        "UiAvTWVkaWFCb3ggWzAgMCA0MDAgMzAwXSAvUmVzb3VyY2VzIDw8IC9Gb250IDw8IC9GMSA1IDAgUiA+PiA+PiAvQ29udGVudHMg" +
        "NCAwIFIgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA1NiA+PgpzdHJlYW0KAt7ghBn3yu37rZDWUpsnKucP49Ar5YldoQQ4" +
        "1oNtZB25E9ft2d2z9ln4XvJgh6Xl7hT5YMvG39EKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8IC9UeXBlIC9Gb250IC9TdWJ0" +
        "eXBlIC9UeXBlMSAvQmFzZUZvbnQgL0hlbHZldGljYSA+PgplbmRvYmoKNiAwIG9iago8PCAvRmlsdGVyIC9TdGFuZGFyZCAvViAx" +
        "IC9SIDIgL0xlbmd0aCA0MCAvTyA8RjhEMUI2QzRDODIxNjhFMzczQzY4QUM1N0REODQ5OEMyRDdERUI4N0ZFOTU0QkQ0ODBBQ0VG" +
        "RTdBNUQ0REJBMT4gL1UgPDNEMzI3MEVFNkY2RTNCNTVGOTNCOUM4QkUyQjZGNURDNzVCNjVBNkZDMUJGRUIxMjkwOUZBQzRBMTEy" +
        "OUVGNTA+IC9QIC00ID4+CmVuZG9iagp4cmVmCjAgNwowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMDkgMDAwMDAgbiAKMDAw" +
        "MDAwMDA1OCAwMDAwMCBuIAowMDAwMDAwMTE1IDAwMDAwIG4gCjAwMDAwMDAyNDEgMDAwMDAgbiAKMDAwMDAwMDM0NyAwMDAwMCBu" +
        "IAowMDAwMDAwNDE3IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1NpemUgNyAvUm9vdCAxIDAgUiAvRW5jcnlwdCA2IDAgUiAvSUQgWzw1" +
        "MzU5NEU1NDQ4NDU1NDQ5NDMyRDQ2NDk0QzQ1MkQ0OTQ0Pjw1MzU5NEU1NDQ4NDU1NDQ5NDMyRDQ2NDk0QzQ1MkQ0OTQ0Pl0gPj4K" +
        "c3RhcnR4cmVmCjYyMwolJUVPRgo=";
}
