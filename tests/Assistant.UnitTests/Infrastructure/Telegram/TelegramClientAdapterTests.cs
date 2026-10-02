using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Infrastructure.Telegram;
using Telegram.Bot;

namespace Assistant.UnitTests.Infrastructure.Telegram;

public class TelegramClientAdapterTests
{
    private readonly RecordingHandler _handler = new();

    private TelegramClientAdapter CreateAdapter() =>
        new(new TelegramBotClient(new TelegramBotClientOptions("123456:test-token"), new HttpClient(_handler)));

    [Fact]
    public async Task SetReactionAsync_sends_one_emoji_reaction()
    {
        await CreateAdapter().SetReactionAsync(-100, 42, "✍", CancellationToken.None);

        _handler.LastUri.ShouldNotBeNull().AbsolutePath.ShouldEndWith("/setMessageReaction");
        using var body = JsonDocument.Parse(_handler.LastBody.ShouldNotBeNull());
        body.RootElement.GetProperty("chat_id").GetInt64().ShouldBe(-100);
        body.RootElement.GetProperty("message_id").GetInt32().ShouldBe(42);
        var reaction = body.RootElement.GetProperty("reaction");
        reaction.GetArrayLength().ShouldBe(1);
        reaction[0].GetProperty("type").GetString().ShouldBe("emoji");
        reaction[0].GetProperty("emoji").GetString().ShouldBe("✍");
    }

    [Fact]
    public async Task SetReactionAsync_with_null_clears_the_reaction()
    {
        await CreateAdapter().SetReactionAsync(-100, 42, null, CancellationToken.None);

        _handler.LastUri.ShouldNotBeNull().AbsolutePath.ShouldEndWith("/setMessageReaction");
        using var body = JsonDocument.Parse(_handler.LastBody.ShouldNotBeNull());
        body.RootElement.GetProperty("message_id").GetInt32().ShouldBe(42);
        if (body.RootElement.TryGetProperty("reaction", out var reaction))
        {
            reaction.GetArrayLength().ShouldBe(0);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json")
            };
        }
    }
}
