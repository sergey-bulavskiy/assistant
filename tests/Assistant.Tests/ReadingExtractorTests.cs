using System.Net;
using System.Text;
using System.Text.Json;
using Assistant;
using Xunit;

namespace Assistant.Tests;

public sealed class ReadingExtractorTests
{
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Result(string payload) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = payload } } } }
    });

    [Fact]
    public async Task ReturnsExactReadingAndRequestsStatelessStructuredOutput()
    {
        var handler = new Handler(Result("{\"value\":7.8,\"unit\":\"mmol/L\"}"));
        using var http = new HttpClient(handler);
        var extractor = new ReadingExtractor(http, "synthetic-key", "synthetic-model");

        var reading = await extractor.ExtractAsync("Reading is 7.8 mmol/L", CancellationToken.None);

        Assert.Equal(new Reading(7.8m, "mmol/L"), reading);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("synthetic-model", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", request.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{\"value\":null,\"unit\":null}")]
    [InlineData("{\"value\":7.8,\"unit\":null}")]
    [InlineData("{\"value\":0,\"unit\":\"mmol/L\"}")]
    [InlineData("{\"value\":7.8,\"unit\":\"unknown\"}")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task InvalidModelResultIsRejected(string payload)
    {
        using var http = new HttpClient(new Handler(Result(payload)));
        Assert.Null(await new ReadingExtractor(http, "synthetic-key", "synthetic-model")
            .ExtractAsync("synthetic input", CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"declined\"}]}]}")]
    public async Task IncompleteOrRefusedResponseIsRejected(string body)
    {
        using var http = new HttpClient(new Handler(body));
        Assert.Null(await new ReadingExtractor(http, "synthetic-key", "synthetic-model")
            .ExtractAsync("synthetic input", CancellationToken.None));
    }

    [Fact]
    public async Task BlankInputDoesNotCallProvider()
    {
        var handler = new Handler("{}");
        using var http = new HttpClient(handler);
        Assert.Null(await new ReadingExtractor(http, "synthetic-key", "synthetic-model")
            .ExtractAsync(" ", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ProviderFailureThrowsWithoutLeakingResponseBody(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler("synthetic sensitive response", status));
        var extractor = new ReadingExtractor(http, "synthetic-key", "synthetic-model");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => extractor.ExtractAsync("synthetic input", CancellationToken.None));

        Assert.Equal("OpenAI request failed.", error.Message);
    }
}
