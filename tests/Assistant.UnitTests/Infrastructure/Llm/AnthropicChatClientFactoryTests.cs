using System.Net;
using Assistant.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class AnthropicChatClientFactoryTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static IChatClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var httpClient = new HttpClient(handler);
        return AnthropicChatClientFactory.Create("test-key", httpClient, "claude-haiku-4-5", TimeSpan.FromSeconds(30));
    }

    // --- B1: a genuine rate limit (rate_limit_error, no spend-cap/usage-limit marker) ----------

    [Fact]
    public async Task A_429_rate_limit_error_with_Retry_After_maps_to_a_model_scoped_limit_exception_with_that_retry_time()
    {
        var client = ClientFor(_ =>
        {
            var response = JsonResponse((HttpStatusCode)429,
                """{"type":"error","error":{"type":"rate_limit_error","message":"rate limited"}}""");
            response.Headers.Add("Retry-After", "30");
            return response;
        });

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
        ex.RetryAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_429_rate_limit_error_with_no_Retry_After_and_no_spend_marker_is_still_model_scoped()
    {
        var client = ClientFor(_ => JsonResponse((HttpStatusCode)429,
            """{"type":"error","error":{"type":"rate_limit_error","message":"rate limited"}}"""));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
        ex.RetryAt.ShouldBeNull();
    }

    // --- B1: a tier spend-cap hit -- same error.type, no retry-after, but a distinct error code
    // (https://platform.claude.com/docs/en/api/rate-limits#reaching-your-spend-cap) ------------

    [Fact]
    public async Task A_429_with_enforced_spend_limit_error_code_maps_to_a_provider_scoped_limit_exception_with_no_retry_time()
    {
        var client = ClientFor(_ =>
        {
            var response = JsonResponse((HttpStatusCode)429,
                """
                {"type":"error","error":{"type":"rate_limit_error","message":"You have reached your API usage limits: your organization has crossed its monthly API usage threshold.","details":{"error_code":"enforced_spend_limit_reached"}}}
                """);
            // The docs state this response carries no retry-after header at all; add one anyway to
            // prove classification ignores it once the error code marks this Provider-scoped.
            response.Headers.Add("Retry-After", "30");
            return response;
        });

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    // --- B1: a workspace-set spend *limit* -- a 400 invalid_request_error whose message names the
    // limit (https://platform.claude.com/docs/en/api/rate-limits#setting-your-own-spend-limit) ---

    [Fact]
    public async Task A_400_with_a_specified_usage_limit_message_maps_to_a_provider_scoped_limit_exception()
    {
        var client = ClientFor(_ => JsonResponse(HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"You have reached your specified API usage limits."}}"""));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    // --- B1: the older pay-as-you-go "credit balance is too low" shape, also a 400 -------------

    [Fact]
    public async Task A_400_with_a_credit_balance_too_low_message_maps_to_a_provider_scoped_limit_exception()
    {
        var client = ClientFor(_ => JsonResponse(HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Claude API. Please go to Plans & Billing to upgrade or purchase credits."}}"""));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Provider);
        ex.RetryAt.ShouldBeNull();
    }

    // --- Review nit: a non-string "type"/"message"/"error_code" must not throw out of
    // classification (JsonElement.GetString() throws InvalidOperationException for a non-string
    // value) -- it should fall through to the conservative per-status default instead.

    [Fact]
    public async Task A_429_with_a_non_string_error_type_does_not_throw_and_is_treated_as_model_scoped()
    {
        var client = ClientFor(_ => JsonResponse((HttpStatusCode)429,
            """{"type":"error","error":{"type":123,"message":"rate limited"}}"""));

        var ex = await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        ex.Scope.ShouldBe(LlmLimitScope.Model);
    }

    [Fact]
    public async Task An_ordinary_400_without_a_limit_message_passes_through_unchanged()
    {
        var client = ClientFor(_ => JsonResponse(HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad request"}}"""));

        var ex = await Should.ThrowAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        ex.ShouldNotBeOfType<ModelLimitReachedException>();
    }

    [Fact]
    public async Task A_non_limit_error_passes_through_unchanged()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"api_error","message":"boom"}}""")
        });

        var ex = await Should.ThrowAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        ex.ShouldNotBeOfType<ModelLimitReachedException>();
    }

    // --- S1: the original 429 response must be disposed, not leaked, before throwing -----------

    private sealed class DisposeTrackingResponse : HttpResponseMessage
    {
        public bool Disposed { get; private set; }

        public DisposeTrackingResponse(HttpStatusCode status) : base(status)
        {
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // --- Review nit: group authorship folded into the text for this provider (no per-message author
    // field in the Anthropic Messages API, unlike claude-cli's own <msg author> framing) ----------

    [Fact]
    public async Task A_message_with_an_author_name_is_folded_into_the_text_as_a_bracketed_prefix()
    {
        string? capturedBody = null;
        var client = ClientFor(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],
                     "model":"claude-haiku-4-5","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
                    """)
            };
        });

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") { AuthorName = "alice" } });

        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("[alice]: hi");
    }

    [Fact]
    public async Task A_429_disposes_the_original_response_before_throwing()
    {
        var tracked = new DisposeTrackingResponse((HttpStatusCode)429)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"rate_limit_error","message":"rate limited"}}""")
        };
        var client = ClientFor(_ => tracked);

        await Should.ThrowAsync<ModelLimitReachedException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        tracked.Disposed.ShouldBeTrue();
    }
}
