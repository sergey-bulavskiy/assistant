using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Assistant.Application.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using SkiaSharp;

namespace Assistant.UnitTests.Vet.Photos;

public sealed class VetPhotoImageDecoderTests
{
    private readonly VetPhotoImageDecoder _decoder = new();

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    public void Full_static_decode_reports_exact_dimensions_and_keeps_original_bytes(SKEncodedImageFormat format, string mime)
    {
        var input = Encode(format, 32, 24);
        var before = input.ToArray();
        var decoded = _decoder.Decode(input, CancellationToken.None);
        decoded.FailureReason.ShouldBeNull();
        decoded.Success.ShouldBeTrue();
        var image = decoded.Image.ShouldNotBeNull();
        image.Width.ShouldBe(32); image.Height.ShouldBe(24); image.MimeType.ShouldBe(mime);
        image.EncodedBytes.ShouldBe(input.LongLength);
        image.DecodedRgbaBytes.ShouldBe(3072);
        input.ShouldBe(before);
    }

    [Fact]
    public void Jpeg_orientation_metadata_does_not_rotate_or_replace_original()
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, 32, 24);
        byte[] exif = [69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0];
        var payload = new byte[exif.Length + 4]; payload[0] = 255; payload[1] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)(exif.Length + 2)); exif.CopyTo(payload, 4);
        var input = jpeg[..2].Concat(payload).Concat(jpeg[2..]).ToArray();
        var before = input.ToArray();
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Success.ShouldBeTrue(); result.Image!.Width.ShouldBe(32); result.Image.Height.ShouldBe(24);
        input.ShouldBe(before);
    }

    [Theory]
    [InlineData(20001, 1)]
    [InlineData(1, 20001)]
    public void Thin_png_panorama_has_no_artificial_ten_thousand_dimension_limit(int width, int height)
    {
        var input = Png(width, height, Rows(width, height));
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Success.ShouldBeTrue(); result.Image!.Width.ShouldBe(width); result.Image.Height.ShouldBe(height);
        result.Image.DecodedRgbaBytes.ShouldBe(80004);
    }

    [Fact]
    public void Exactly_twenty_five_megapixels_decode_but_next_pixel_is_refused()
    {
        // 100MB transient RGBA is allocated/disposed only by the exact-boundary case.
        var at = _decoder.Decode(Png(5000, 5000, Rows(5000, 5000)), CancellationToken.None);
        at.Success.ShouldBeTrue(); at.Image!.DecodedRgbaBytes.ShouldBe(100_000_000);
        var above = _decoder.Decode(Png(25_000_001, 1, [0, 0, 0, 0, 0]), CancellationToken.None);
        above.Image.ShouldBeNull(); above.FailureReason.ShouldBe("decoded_image_too_large");
    }

    [Fact]
    public void Encoded_input_boundary_accepts_exact_cap_and_refuses_one_extra_byte()
    {
        var png = Png(1, 1, [0, 0, 0, 0, 255]);
        // Safe unknown ancillary content remains subject to the explicit metadata bound,
        // so IDAT uses uncompressed deflate blocks padded by real zero-length IDAT chunks.
        var padded = PadWithEmptyIdat(png, VetPhotoImageLimits.MaxEncodedBytes);
        var at = _decoder.Decode(padded, CancellationToken.None);
        at.Success.ShouldBeTrue(); at.Image!.EncodedBytes.ShouldBe(10_485_760);
        var above = _decoder.Decode(padded.Concat(new byte[] { 0 }).ToArray(), CancellationToken.None);
        above.Image.ShouldBeNull(); above.FailureReason.ShouldBe("encoded_image_too_large");
    }

    [Fact]
    public void Unsupported_signature_and_empty_input_have_fixed_refusals()
    {
        _decoder.Decode("GIF89a synthetic"u8.ToArray(), CancellationToken.None).FailureReason.ShouldBe("unsupported_image");
        _decoder.Decode(ReadOnlyMemory<byte>.Empty, CancellationToken.None).FailureReason.ShouldBe("invalid_image");
    }

    [Fact]
    public void Truncated_png_or_jpeg_never_becomes_successful_partial_decode()
    {
        var png = Png(2, 2, Rows(2, 2));
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, 32, 24);
        foreach (var input in new[] { png[..^1], png[..33], jpeg[..^2], jpeg[..20] })
        {
            var result = _decoder.Decode(input, CancellationToken.None);
            result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("invalid_image");
        }
    }

    [Fact]
    public void Bad_ancillary_crc_is_refused_before_native_decoder_can_ignore_it()
    {
        var text = Chunk("tEXt", "label\0synthetic"u8.ToArray()); text[^1] ^= 1;
        var input = Png(1, 1, [0, 0, 0, 0, 255], extra: [text]);
        var before = input.ToArray();
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("invalid_image"); input.ShouldBe(before);
    }

    [Theory]
    [InlineData("iCCP")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public void Compressed_metadata_expansion_bomb_is_refused_with_bounded_category(string type)
    {
        var compressed = Zlib(new byte[1_048_577]);
        var prefix = type == "iTXt" ? "label\0\x01\0\0\0"u8.ToArray() : "label\0\0"u8.ToArray();
        var input = Png(1, 1, [0, 0, 0, 0, 255], extra: [Chunk(type, prefix.Concat(compressed).ToArray())]);
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("image_metadata_too_large");
    }

    [Fact]
    public void Aggregate_metadata_cap_counts_multiple_individually_small_compressed_chunks()
    {
        var payload = "label\0\0"u8.ToArray().Concat(Zlib(new byte[600_000])).ToArray();
        var input = Png(1, 1, [0, 0, 0, 0, 255], extra: [Chunk("zTXt", payload), Chunk("zTXt", payload)]);
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("image_metadata_too_large");
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public void Valid_compressed_metadata_below_cap_keeps_complete_png_and_original_bytes(string type)
    {
        var compressed = Zlib(Encoding.UTF8.GetBytes("synthetic annotation"));
        var prefix = type == "iTXt" ? "label\0\x01\0en\0translated\0"u8.ToArray() : "label\0\0"u8.ToArray();
        var input = Png(2, 2, Rows(2, 2), extra: [Chunk(type, prefix.Concat(compressed).ToArray())]);
        var before = input.ToArray();
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Success.ShouldBeTrue(); result.FailureReason.ShouldBeNull();
        result.Image!.Width.ShouldBe(2); result.Image.Height.ShouldBe(2); result.Image.MimeType.ShouldBe("image/png");
        input.ShouldBe(before);
    }

    [Fact]
    public void Many_plain_ancillary_chunks_share_the_same_aggregate_metadata_cap()
    {
        var payload = "label\0"u8.ToArray().Concat(new byte[600_000]).ToArray();
        var result = _decoder.Decode(Png(1, 1, [0,0,0,0,255], extra: [Chunk("tEXt", payload), Chunk("tEXt", payload)]),
            CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("image_metadata_too_large");
    }

    [Fact]
    public void Idat_expansion_bomb_cannot_allocate_image_from_false_small_header()
    {
        var input = Png(1, 1, new byte[4096]);
        var result = _decoder.Decode(input, CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("decoded_image_too_large");
    }

    [Theory]
    [InlineData("short_rows")]
    [InlineData("invalid_filter")]
    [InlineData("bad_adler")]
    [InlineData("bad_zlib_header")]
    public void Malformed_scanline_or_deflate_content_is_refused(string malformed)
    {
        var rows = malformed == "short_rows" ? new byte[] { 0, 1 } : new byte[] { 0, 0, 0, 0, 255 };
        if (malformed == "invalid_filter") rows[0] = 5;
        var compressed = Zlib(rows);
        if (malformed == "bad_adler") compressed[^1] ^= 1;
        if (malformed == "bad_zlib_header") compressed[0] = 0;
        var result = _decoder.Decode(Png(1, 1, rows, compressed: compressed), CancellationToken.None);
        result.Image.ShouldBeNull(); result.FailureReason.ShouldBe("invalid_image");
    }

    [Fact]
    public void Split_idat_stream_is_joined_without_turning_chunks_into_frames()
    {
        var compressed = Zlib(Rows(2, 2));
        var chunks = new[] { Chunk("IDAT", compressed[..2]), Chunk("IDAT", compressed[2..^3]), Chunk("IDAT", compressed[^3..]) };
        var result = _decoder.Decode(Png(2, 2, [], dataChunks: chunks), CancellationToken.None);
        result.Success.ShouldBeTrue(); result.Image!.Width.ShouldBe(2); result.Image.Height.ShouldBe(2);
    }

    [Fact]
    public void Adam7_and_sixteen_bit_png_layouts_decode_with_checked_rgba_output()
    {
        byte[] adam7 = [0,12,34,56,255, 0,12,34,56,255, 0,12,34,56,255,12,34,56,255];
        var interlaced = _decoder.Decode(Png(2, 2, adam7, interlace: 1), CancellationToken.None);
        interlaced.Success.ShouldBeTrue(); interlaced.Image!.DecodedRgbaBytes.ShouldBe(16);
        byte[] sixteen = [0,0,12,0,34,0,56,255,255];
        var highDepth = _decoder.Decode(Png(1, 1, sixteen, depth: 16), CancellationToken.None);
        highDepth.Success.ShouldBeTrue(); highDepth.Image!.DecodedRgbaBytes.ShouldBe(4);
    }

    [Fact]
    public void Animation_and_malformed_chunk_structure_are_refused()
    {
        var animated = Png(1, 1, [0, 0, 0, 0, 255], extra: [Chunk("acTL", [0,0,0,1,0,0,0,0])]);
        _decoder.Decode(animated, CancellationToken.None).FailureReason.ShouldBe("unsupported_image");
        var valid = Png(1, 1, [0, 0, 0, 0, 255]);
        var badLength = valid.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(8, 4), uint.MaxValue);
        _decoder.Decode(badLength, CancellationToken.None).FailureReason.ShouldBe("invalid_image");
        _decoder.Decode(valid.Concat(new byte[] { 1 }).ToArray(), CancellationToken.None).FailureReason.ShouldBe("invalid_image");
    }

    [Fact]
    public void Caller_cancellation_is_propagated_without_transforming_input()
    {
        var input = Png(1, 1, [0, 0, 0, 0, 255]); var before = input.ToArray();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => _decoder.Decode(input, cancelled.Token));
        input.ShouldBe(before);
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    private static byte[] Rows(int width, int height)
    {
        var rowBytes = checked(width * 4 + 1); var rows = new byte[checked(rowBytes * height)];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++) rows[row * rowBytes + 1 + column * 4 + 3] = 255;
        return rows;
    }

    private static byte[] Png(int width, int height, byte[] rows, int depth = 8, int interlace = 0,
        byte[][]? extra = null, byte[]? compressed = null, byte[][]? dataChunks = null)
    {
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = (byte)depth; header[9] = 6; header[12] = (byte)interlace;
        using var output = new MemoryStream(); output.Write(new byte[] {137,80,78,71,13,10,26,10});
        output.Write(Chunk("IHDR", header));
        foreach (var chunk in extra ?? []) output.Write(chunk);
        foreach (var chunk in dataChunks ?? [Chunk("IDAT", compressed ?? Zlib(rows))]) output.Write(chunk);
        output.Write(Chunk("IEND", [])); return output.ToArray();
    }

    private static byte[] Zlib(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) compressor.Write(plain);
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] payload)
    {
        var result = new byte[payload.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)payload.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4); payload.CopyTo(result, 8);
        uint crc = uint.MaxValue;
        foreach (var value in result.AsSpan(4, payload.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(payload.Length + 8, 4), ~crc); return result;
    }

    private static byte[] PadWithEmptyIdat(byte[] png, int length)
    {
        // PNG accepts zero-length IDAT chunks. Reach the exact encoded boundary using those
        // chunks plus empty stored blocks in the single valid zlib stream inside the original IDAT.
        // Pick one of 0–11 additional zlib empty blocks (five bytes each) to make padding /12 exact.
        var originalIdatOffset = 33;
        var originalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(originalIdatOffset, 4));
        var compressed = png.AsSpan(originalIdatOffset + 8, originalLength).ToArray();
        for (var blocks = 0; blocks < 12; blocks++)
        {
            // Rebuild an equivalent zlib stream using nonfinal empty stored blocks followed by
            // one final uncompressed block containing the known one-pixel scanline.
            using var z = new MemoryStream(); z.Write(new byte[] { 0x78, 0x01 });
            for (var i = 0; i < blocks; i++) z.Write(new byte[] { 0, 0, 0, 255, 255 });
            z.Write(new byte[] { 1, 5, 0, 250, 255, 0, 0, 0, 0, 255 });
            z.Write(compressed[^4..]);
            var rebuilt = Png(1, 1, [], compressed: z.ToArray());
            var difference = length - rebuilt.Length;
            if (difference < 0 || difference % 12 != 0) continue;
            var empty = Chunk("IDAT", []);
            using var result = new MemoryStream(length); result.Write(rebuilt[..^12]);
            for (var i = 0; i < difference / 12; i++) result.Write(empty);
            result.Write(rebuilt[^12..]); return result.ToArray();
        }
        throw new InvalidOperationException("synthetic PNG padding could not reach exact boundary");
    }
}
