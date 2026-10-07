using System.Buffers.Binary;
using System.IO.Compression;
using Assistant.Application.Vet.Photos;
using SkiaSharp;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed class VetPhotoImageDecoder : IVetPhotoImageDecoder
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public VetPhotoImageDecodeResult Decode(ReadOnlyMemory<byte> original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (original.Length > VetPhotoImageLimits.MaxEncodedBytes) return Refuse("encoded_image_too_large");
        if (original.IsEmpty) return Refuse("invalid_image");
        try
        {
            var bytes = original.ToArray();
            var png = bytes.AsSpan().StartsWith(PngSignature);
            var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            if (!png && !jpeg) return Refuse("unsupported_image");
            (int Width, int Height)? pngDimensions = png ? ValidatePng(bytes, token) : null;
            if (jpeg && (bytes.Length < 4 || bytes[^2] != 0xff || bytes[^1] != 0xd9)) return Refuse("invalid_image");
            token.ThrowIfCancellationRequested();
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) return Refuse("invalid_image");
            var format = codec.EncodedFormat;
            if (format != (png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg)) return Refuse("invalid_image");
            // Skia reports zero animation frames for static PNG/JPEG codecs.
            if (codec.FrameCount > 1) return Refuse("unsupported_image");
            var info = codec.Info;
            using var sourceColorSpace = info.ColorSpace;
            var rgbaBytes = CheckDimensions(info.Width, info.Height);
            if (pngDimensions is { } declared && (declared.Width != info.Width || declared.Height != info.Height))
                return Refuse("invalid_image");
            var target = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (checked((long)target.RowBytes * target.Height) != rgbaBytes) return Refuse("decoded_image_too_large");
            token.ThrowIfCancellationRequested();
            using var bitmap = new SKBitmap();
            if (!bitmap.TryAllocPixels(target)) throw new OutOfMemoryException("Photo decoder allocation unavailable.");
            var decoded = codec.GetPixels(target, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions());
            token.ThrowIfCancellationRequested();
            if (decoded != SKCodecResult.Success) return Refuse("invalid_image");
            return new(new(info.Width, info.Height, png ? "image/png" : "image/jpeg", bytes.LongLength, rgbaBytes), null);
        }
        catch (ImageRefused exception) { return Refuse(exception.Category); }
        catch (InvalidDataException) { return Refuse("invalid_image"); }
        catch (OverflowException) { return Refuse("decoded_image_too_large"); }
        catch (ArgumentException) { return Refuse("invalid_image"); }
        catch (DllNotFoundException) { return Refuse("decoder_unavailable"); }
        catch (EntryPointNotFoundException) { return Refuse("decoder_unavailable"); }
        catch (BadImageFormatException) { return Refuse("decoder_unavailable"); }
        catch (TypeInitializationException exception) when (exception.InnerException is DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException) { return Refuse("decoder_unavailable"); }
    }

    private static VetPhotoImageDecodeResult Refuse(string reason) => new(null, reason);

    private static long CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ImageRefused("invalid_image");
        var pixels = checked((long)width * height);
        var bytes = checked(pixels * 4);
        if (pixels > VetPhotoImageLimits.MaxPixels || bytes > VetPhotoImageLimits.MaxRgbaBytes)
            throw new ImageRefused("decoded_image_too_large");
        return bytes;
    }

    private static (int Width, int Height) ValidatePng(byte[] bytes, CancellationToken token)
    {
        var position = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var color = 0;
        var interlace = 0;
        var header = false;
        var palette = false;
        var dataStarted = false;
        var dataEnded = false;
        var end = false;
        long metadata = 0;
        var imageData = new List<Segment>();
        while (position < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length - position < 12) throw new ImageRefused("invalid_image");
            var unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (unsignedLength > int.MaxValue || unsignedLength > bytes.Length - position - 12)
                throw new ImageRefused("invalid_image");
            var length = (int)unsignedLength;
            var type = bytes.AsSpan(position + 4, 4);
            foreach (var letter in type)
                if (!(letter >= 'A' && letter <= 'Z' || letter >= 'a' && letter <= 'z'))
                    throw new ImageRefused("invalid_image");
            if ((type[2] & 32) != 0) throw new ImageRefused("invalid_image");
            var actualCrc = Crc(bytes.AsSpan(position + 4, length + 4), token);
            if (actualCrc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length, 4)))
                throw new ImageRefused("invalid_image");
            var payload = bytes.AsSpan(position + 8, length);
            var name = System.Text.Encoding.ASCII.GetString(type);
            if (!header && name != "IHDR") throw new ImageRefused("invalid_image");
            if (end) throw new ImageRefused("invalid_image");
            if (name == "IHDR")
            {
                if (header || length != 13) throw new ImageRefused("invalid_image");
                var w = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                if (w > int.MaxValue || h > int.MaxValue) throw new ImageRefused("decoded_image_too_large");
                width = (int)w; height = (int)h;
                CheckDimensions(width, height);
                bitDepth = payload[8]; color = payload[9]; interlace = payload[12];
                var legalDepth = color switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => bitDepth is 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!legalDepth || payload[10] != 0 || payload[11] != 0 || interlace is not (0 or 1))
                    throw new ImageRefused("invalid_image");
                header = true;
            }
            else if (name == "PLTE")
            {
                if (palette || dataStarted || color is 0 or 4 || length is < 3 or > 768 || length % 3 != 0
                    || color == 3 && length / 3 > (1 << bitDepth)) throw new ImageRefused("invalid_image");
                palette = true;
            }
            else if (name == "IDAT")
            {
                if (dataEnded || color == 3 && !palette) throw new ImageRefused("invalid_image");
                dataStarted = true;
                if (length > 0) imageData.Add(new(position + 8, length));
            }
            else if (name == "IEND")
            {
                if (!dataStarted || length != 0) throw new ImageRefused("invalid_image");
                end = true;
            }
            else
            {
                if (dataStarted) dataEnded = true;
                if (name is "acTL" or "fcTL" or "fdAT") throw new ImageRefused("unsupported_image");
                if ((type[0] & 32) == 0) throw new ImageRefused("invalid_image");
                metadata = checked(metadata + length + 12L);
                if (metadata > VetPhotoImageLimits.MaxPngMetadataBytes) throw new ImageRefused("image_metadata_too_large");
                var compressedOffset = MetadataCompressedOffset(name, payload);
                if (compressedOffset is { } offset)
                {
                    var segment = new Segment(position + 8 + offset, length - offset);
                    metadata += Inflate(bytes, [segment], VetPhotoImageLimits.MaxPngMetadataBytes - metadata,
                        "image_metadata_too_large", null, token);
                }
            }
            position += length + 12;
        }
        if (!end || imageData.Count == 0) throw new ImageRefused("invalid_image");
        var scanlines = new Scanlines(width, height, bitDepth, color, interlace);
        var expanded = Inflate(bytes, imageData, scanlines.ExpectedBytes, "decoded_image_too_large", scanlines, token);
        if (expanded != scanlines.ExpectedBytes || !scanlines.Complete) throw new ImageRefused("invalid_image");
        return (width, height);
    }

    private static int? MetadataCompressedOffset(string name, ReadOnlySpan<byte> payload)
    {
        if (name is not ("iCCP" or "zTXt" or "iTXt")) return null;
        var keywordEnd = payload.IndexOf((byte)0);
        if (keywordEnd is < 1 or > 79 || keywordEnd + 2 > payload.Length) throw new ImageRefused("invalid_image");
        if (name != "iTXt")
        {
            if (payload[keywordEnd + 1] != 0) throw new ImageRefused("invalid_image");
            return keywordEnd + 2;
        }
        if (keywordEnd + 3 > payload.Length || payload[keywordEnd + 1] > 1 || payload[keywordEnd + 2] != 0)
            throw new ImageRefused("invalid_image");
        var languageStart = keywordEnd + 3;
        var languageLength = payload[languageStart..].IndexOf((byte)0);
        if (languageLength < 0) throw new ImageRefused("invalid_image");
        var translatedStart = languageStart + languageLength + 1;
        var translatedLength = payload[translatedStart..].IndexOf((byte)0);
        if (translatedLength < 0) throw new ImageRefused("invalid_image");
        return payload[keywordEnd + 1] == 1 ? translatedStart + translatedLength + 1 : null;
    }

    private static long Inflate(byte[] bytes, IReadOnlyList<Segment> segments, long limit,
        string excessCategory, Scanlines? scanlines, CancellationToken token)
    {
        using var input = new SegmentStream(bytes, segments);
        if (input.Length < 6) throw new ImageRefused("invalid_image");
        var cmf = ByteAt(bytes, segments, 0);
        var flg = ByteAt(bytes, segments, 1);
        if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 32) != 0)
            throw new ImageRefused("invalid_image");
        uint expectedAdler = 0;
        for (var index = 4; index > 0; index--) expectedAdler = (expectedAdler << 8) | ByteAt(bytes, segments, input.Length - index);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        var scratch = new byte[8192];
        long total = 0;
        uint a = 1, b = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = zlib.Read(scratch, 0, scratch.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > limit) throw new ImageRefused(excessCategory);
            for (var i = 0; i < read; i++) { a = (a + scratch[i]) % 65521; b = (b + a) % 65521; }
            scanlines?.Consume(scratch.AsSpan(0, read));
        }
        if (((b << 16) | a) != expectedAdler) throw new ImageRefused("invalid_image");
        return total;
    }

    private static byte ByteAt(byte[] bytes, IReadOnlyList<Segment> segments, long index)
    {
        foreach (var segment in segments)
        {
            if (index < segment.Length) return bytes[segment.Offset + (int)index];
            index -= segment.Length;
        }
        throw new ImageRefused("invalid_image");
    }

    private static uint Crc(ReadOnlySpan<byte> data, CancellationToken token)
    {
        uint crc = uint.MaxValue;
        for (var index = 0; index < data.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            crc ^= data[index];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }

    private sealed class Scanlines
    {
        private readonly List<(long Bytes, long Rows)> _passes = [];
        private int _pass;
        private long _rows;
        private long _remaining;
        private bool _filter = true;
        public long ExpectedBytes { get; }
        public bool Complete => _pass == _passes.Count && _filter;
        public Scanlines(int width, int height, int depth, int color, int interlace)
        {
            var channels = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new ImageRefused("invalid_image") };
            if (interlace == 0) Add(width, height, channels * depth);
            else
            {
                int[] x = [0, 4, 0, 2, 0, 1, 0], y = [0, 0, 4, 0, 2, 0, 1];
                int[] dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
                for (var i = 0; i < 7; i++)
                    Add(width <= x[i] ? 0 : ((long)width - x[i] + dx[i] - 1) / dx[i],
                        height <= y[i] ? 0 : ((long)height - y[i] + dy[i] - 1) / dy[i], channels * depth);
            }
            ExpectedBytes = _passes.Sum(p => checked((p.Bytes + 1) * p.Rows));
            _rows = _passes.Count > 0 ? _passes[0].Rows : 0;
        }
        private void Add(long width, long height, int bits)
        { if (width > 0 && height > 0) _passes.Add((checked((width * bits + 7) / 8), height)); }
        public void Consume(ReadOnlySpan<byte> data)
        {
            var position = 0;
            while (position < data.Length)
            {
                if (_pass == _passes.Count) throw new ImageRefused("decoded_image_too_large");
                if (_filter)
                {
                    if (data[position++] > 4) throw new ImageRefused("invalid_image");
                    _remaining = _passes[_pass].Bytes;
                    _filter = false;
                }
                var take = (int)Math.Min(_remaining, data.Length - position);
                _remaining -= take; position += take;
                if (_remaining == 0)
                {
                    _filter = true;
                    if (--_rows == 0)
                    {
                        _pass++;
                        if (_pass < _passes.Count) _rows = _passes[_pass].Rows;
                    }
                }
            }
        }
    }

    private sealed record Segment(int Offset, int Length);
    private sealed class SegmentStream(byte[] bytes, IReadOnlyList<Segment> segments) : Stream
    {
        private int _segment;
        private int _offset;
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = segments.Sum(s => (long)s.Length);
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var copied = 0;
            while (!buffer.IsEmpty && _segment < segments.Count)
            {
                var segment = segments[_segment];
                var take = Math.Min(buffer.Length, segment.Length - _offset);
                bytes.AsSpan(segment.Offset + _offset, take).CopyTo(buffer);
                buffer = buffer[take..]; copied += take; _offset += take; _position += take;
                if (_offset == segment.Length) { _segment++; _offset = 0; }
            }
            return copied;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ImageRefused(string category) : Exception
    { public string Category { get; } = category; }
}
