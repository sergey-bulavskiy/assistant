using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.UnitTests.Infrastructure.Telegram;

public sealed class TelegramMediaTests
{
    [Fact]
    public void MapsDocumentMetadataAndCaptionWithoutInterpretingFilename()
    {
        var update = JsonSerializer.Deserialize<Update>("""
            {"update_id":1,"message":{"message_id":2,"date":1735000000,
            "chat":{"id":111,"type":"private"},"caption":"synthetic caption",
            "document":{"file_id":"opaque-id","file_unique_id":"unique-id","file_name":"../synthetic.txt",
            "mime_type":"text/plain","file_size":12}}}
            """, JsonBotAPI.Options)!;
        var message = TelegramUpdateMapper.Map(update).Message!;
        message.Document.ShouldBe(new DocumentAttachment("opaque-id", "unique-id", "../synthetic.txt", "text/plain", 12));
        message.Text.ShouldBe("synthetic caption");
        message.Photo.ShouldBeNull();
    }

    [Fact]
    public void MapsPhotoVariantsAlbumAndEditedCaptionAsOneSource()
    {
        var update = JsonSerializer.Deserialize<Update>("""
            {"update_id":3,"edited_message":{"message_id":2,"date":1735000000,"edit_date":1735000010,
            "chat":{"id":111,"type":"private"},"caption":"updated caption","media_group_id":"synthetic-album",
            "photo":[{"file_id":"small","file_unique_id":"s","width":10,"height":20},
            {"file_id":"large","file_unique_id":"l","width":100,"height":200,"file_size":42}]}}
            """, JsonBotAPI.Options)!;
        var message = TelegramUpdateMapper.Map(update).Message!;
        message.IsEdit.ShouldBeTrue();
        message.MediaGroupId.ShouldBe("synthetic-album");
        message.Text.ShouldBe("updated caption");
        message.Photo!.Sizes.ShouldBe(new[]
        {
            new PhotoSizeAttachment("small", "s", 10, 20, null),
            new PhotoSizeAttachment("large", "l", 100, 200, 42)
        });
        message.Document.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2L)]
    [InlineData(4L)]
    public async Task DownloadsExactCapDespiteUnknownOrUnderreportedMetadata(long? reported)
    {
        var handler = new FileHandler([1, 2, 3, 4], reported);
        var adapter = Create(handler);
        using var destination = new MemoryStream();
        (await adapter.DownloadFileAsync("opaque", destination, 4, CancellationToken.None)).ShouldBe(4);
        destination.ToArray().ShouldBe(new byte[] { 1, 2, 3, 4 });
        destination.CanWrite.ShouldBeTrue();
        handler.Downloads.ShouldBe(1);
    }

    [Fact]
    public async Task OversizeMetadataPreventsBodyRequest()
    {
        var handler = new FileHandler([1], 5);
        using var destination = new MemoryStream();
        var error = await Should.ThrowAsync<TelegramFileDownloadException>(
            () => Create(handler).DownloadFileAsync("opaque", destination, 4, CancellationToken.None));
        error.Reason.ShouldBe(TelegramFileDownloadFailure.TooLarge);
        handler.Downloads.ShouldBe(0);
        destination.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ActualOversizeStopsCopyAndLeavesCallerStreamOpen()
    {
        var handler = new FileHandler([1, 2, 3, 4, 5], null);
        using var destination = new MemoryStream();
        var error = await Should.ThrowAsync<TelegramFileDownloadException>(
            () => Create(handler).DownloadFileAsync("opaque", destination, 4, CancellationToken.None));
        error.Reason.ShouldBe(TelegramFileDownloadFailure.TooLarge);
        destination.Length.ShouldBeLessThanOrEqualTo(4);
        destination.CanWrite.ShouldBeTrue();
    }

    [Fact]
    public async Task ProviderErrorDoesNotExposeTokenOrRemoteMessage()
    {
        var handler = new FileHandler([], null) { Fail = true };
        using var destination = new MemoryStream();
        var error = await Should.ThrowAsync<TelegramFileDownloadException>(
            () => Create(handler).DownloadFileAsync("opaque", destination, 4, CancellationToken.None));
        error.Reason.ShouldBe(TelegramFileDownloadFailure.Unavailable);
        error.Message.ShouldNotContain("synthetic-secret");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedWithoutDownload()
    {
        var handler = new FileHandler([1], null);
        using var destination = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            () => Create(handler).DownloadFileAsync("opaque", destination, 4, cancelled.Token));
        handler.Downloads.ShouldBe(0);
        destination.Length.ShouldBe(0);
    }

    private static TelegramClientAdapter Create(FileHandler handler) =>
        new(new TelegramBotClient(new TelegramBotClientOptions("123456:test-token"), new HttpClient(handler)));

    private sealed class FileHandler(byte[] bytes, long? size) : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        public bool Fail { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("synthetic-secret");
            if (request.RequestUri!.AbsolutePath.Contains("/file/", StringComparison.Ordinal))
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            var payload = JsonSerializer.Serialize(new { ok = true, result = new
                { file_id = "opaque", file_unique_id = "unique", file_size = size, file_path = "synthetic.bin" } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
