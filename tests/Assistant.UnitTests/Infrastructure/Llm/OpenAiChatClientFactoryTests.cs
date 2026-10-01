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

    private static HttpResponseMessage Response(string body, bool withRetryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent(body)
        };
        if (withRetryAfter)
        {
            response.Headers.Add("Retry-After", "20");
        }
        return response;
    }

    private static IChatClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var httpClient = new HttpClient(handler);
        return OpenAiChatClientFactory.Create("test-key", httpClient, "gpt-6-luna", baseUrl: null, TimeSpan.FromSeconds(30));
    }

    // Review fix (B1): classify by the body's error code/type, not by Retry-After presence. This is
    // the fixed version of the pre-existing test whose body said "insufficient_quota" yet asserted
    // Model scope -- that assertion was wrong per spec §10.5 amendment 5 (quota/spend-cap errors are
    // Provider-scoped) and is corrected here.
    [Fact]
    public async Task A_429_with_insufficient_quota_body_maps_to_a_provider_scoped_limit_exception_even_with_Retry_After()
    {
        var client = ClientFor(_ => Response(
            """{"error":{"type":"insufficient_quota","code":"insufficient_quota","message":"quota exceeded"}}""",
            withRetryAfter: true));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_429_with_insufficient_quota_type_only_also_maps_to_provider_scope()
    {
        // error.type (not error.code) carrying "insufficient_quota" must be recognized too.
        var client = ClientFor(_ => Response(
            """{"error":{"type":"insufficient_quota","message":"quota exceeded"}}""",
            withRetryAfter: false));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_429_with_a_different_error_body_maps_to_a_model_scoped_limit_exception_with_the_retry_time()
    {
        var client = ClientFor(_ => Response(
            """{"error":{"type":"rate_limit_exceeded","code":"rate_limit_exceeded","message":"slow down"}}""",
            withRetryAfter: true));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
        ex.RetryAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_429_with_a_different_error_body_and_no_Retry_After_is_still_model_scoped_with_no_retry_time()
    {
        var client = ClientFor(_ => Response(
            """{"error":{"type":"rate_limit_exceeded","code":"rate_limit_exceeded","message":"slow down"}}""",
            withRetryAfter: false));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
        ex.RetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_non_limit_error_passes_through_unchanged()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"bad"}}""")
        });

        var ex = await Should.ThrowAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        ex.ShouldNotBeOfType<ModelLimitReachedException>();
    }
}
