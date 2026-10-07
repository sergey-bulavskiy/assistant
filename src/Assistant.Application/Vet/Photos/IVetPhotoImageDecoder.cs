namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoImageDecoder
{
    VetPhotoImageDecodeResult Decode(ReadOnlyMemory<byte> original, CancellationToken token);
}

public sealed record VetPhotoImageInfo(
    int Width, int Height, string MimeType, long EncodedBytes, long DecodedRgbaBytes);

public sealed record VetPhotoImageDecodeResult(VetPhotoImageInfo? Image, string? FailureReason)
{
    public bool Success => Image is not null && FailureReason is null;
}

public static class VetPhotoImageLimits
{
    public const int MaxEncodedBytes = 10_485_760;
    public const long MaxPixels = 25_000_000;
    public const long MaxRgbaBytes = 100_000_000;
    public const long MaxPngMetadataBytes = 1_048_576;
}
