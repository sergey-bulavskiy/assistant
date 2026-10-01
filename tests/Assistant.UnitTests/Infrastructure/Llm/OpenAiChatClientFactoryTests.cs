using System.Net;
using Assistant.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class OpenAiChatClientFactoryTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private static HttpResponseMessage QuotaResponse(bool withRetryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"error":{"type":"insufficient_quota","code":"insufficient_quota","message":"quota exceeded"}}""")
        };
        if (withRetryAfter)
        {
            response.Headers.Add("Retry-After", "20");
        }
        return response;
    }

    [Fact]
    public async Task A_429_with_Retry_After_maps_to_a_model_scoped_limit_exception()
    {
        using var handler = new FakeHandler(_ => QuotaResponse(withRetryAfter: true));
        using var httpClient = new HttpClient(handler);
        var client = OpenAiChatClientFactory.Create("test-key", httpClient, "gpt-6-luna", baseUrl: null, TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
        ex.RetryAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_429_with_no_Retry_After_maps_to_a_provider_scoped_limit_exception()
    {
        using var handler = new FakeHandler(_ => QuotaResponse(withRetryAfter: false));
        using var httpClient = new HttpClient(handler);
        var client = OpenAiChatClientFactory.Create("test-key", httpClient, "gpt-6-luna", baseUrl: null, TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_non_limit_error_passes_through_unchanged()
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"bad"}}""")
        });
        using var httpClient = new HttpClient(handler);
        var client = OpenAiChatClientFactory.Create("test-key", httpClient, "gpt-6-luna", baseUrl: null, TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        ex.ShouldNotBeOfType<ModelLimitReachedException>();
    }
}
