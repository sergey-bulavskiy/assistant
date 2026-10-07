using Assistant.Application.Telegram;

namespace Assistant.Infrastructure.Telegram;

/// <summary>Write-only cap around SDK streaming; disposing it never closes the caller's stream.</summary>
internal sealed class BoundedDownloadStream(Stream destination, long maxBytes) : Stream
{
    public long BytesWritten { get; private set; }
    public bool LimitExceeded { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Check(int count)
    {
        if (count > maxBytes - BytesWritten)
        {
            LimitExceeded = true;
            throw new TelegramFileDownloadException(TelegramFileDownloadFailure.TooLarge);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(count);
        destination.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        destination.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
