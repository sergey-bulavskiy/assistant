using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Builds an Anthropic IChatClient for one catalog entry (Decision 1: one client per
/// entry, model bound at construction via AsIChatClient).
///
/// Task 3 Step 0 found the real 12.51.0 package's shape differs from the plan's Verified facts
/// §A.4/§A.6 in two ways:
/// 1. There is no `new AnthropicClient(HttpClient)` constructor overload -- the custom HttpClient
///    (proxy-aware or plain) is supplied through `Anthropic.Core.ClientOptions.HttpClient`, passed
///    to `new AnthropicClient(ClientOptions)`. `MaxRetries`/`Timeout` are likewise `ClientOptions`
///    members (nullable), not settable properties on a parameterless-constructed client.
/// 2. `AnthropicRateLimitException` (and every other SDK exception) exposes only `StatusCode`,
///    `ResponseBody` (string) and a typed `ErrorType` enum -- no raw headers, and no retry-after
///    accessor of any kind. Touching its `InnerException` getter even throws `ArgumentNullException`
///    when the SDK built the exception without one (confirmed empirically against the real package;
///    never touch it). So the Retry-After header cannot be read off the exception at all.
///    `AsyncLocal&lt;T&gt;` does not bridge this either: a value an inner awaited call sets is not
///    visible to its caller once that inner call returns (confirmed empirically) -- the opposite of
///    what capturing "the last response" into an AsyncLocal would need.
/// The fix: `ClientOptions.Handlers` accepts a chain of `DelegatingHandler`s built by
/// `Anthropic.Core.Handler.Create`, run *inside* the SDK's own HTTP call, before it ever constructs
/// its own exception. A handler here sees the raw `HttpResponseMessage` directly and, on a 429,
/// throws `ModelLimitReachedException` itself (confirmed empirically: an exception thrown from this
/// handler propagates out of `GetResponseAsync` with its exact type intact, not wrapped by the SDK).
/// This sidesteps the concurrency hazard a shared capture field would have for concurrent calls to
/// the same entry's client. `LimitTranslatingChatClient.Translate` below therefore only needs to
/// recognize an already-translated `ModelLimitReachedException` and let it through unchanged; it
/// stays in the pipeline for structural symmetry with the OpenAI provider (Task 4) and so a future,
/// different SDK version's exception shape has one obvious place to translate from instead.</summary>
public static class AnthropicChatClientFactory
{
    public static IChatClient Create(string apiKey, HttpClient httpClient, string modelName, TimeSpan callTimeout)
    {
        var limitHandler = Handler.Create(async (HttpRequestMessage request, NextHandler next, CancellationToken ct) =>
        {
            var response = await next.Invoke(request, ct);
            if ((int)response.StatusCode != 429)
            {
                return response;
            }

            // Verified facts §A.6: Anthropic's 429 body uses "rate_limit_error" for BOTH a true rate
            // limit and an org/workspace spend-cap hit; the documented, confirmed way to tell them
            // apart is whether Retry-After is present at all -- a spend-cap 429 carries none.
            var retryAfter = response.Headers.RetryAfter;
            DateTimeOffset? retryAt = retryAfter?.Delta is { } delta
                ? DateTimeOffset.UtcNow.Add(delta)
                : retryAfter?.Date;

            throw retryAt is { } at
                ? new ModelLimitReachedException("Anthropic rate limit (429, Retry-After present)", LlmLimitScope.Model, at)
                : new ModelLimitReachedException("Anthropic spend/usage limit (429, no Retry-After)", LlmLimitScope.Provider, null);
        });

        var client = new AnthropicClient(new ClientOptions
        {
            HttpClient = httpClient,
            ApiKey = apiKey,
            MaxRetries = 0, // spec §10.5: fallback is immediate, spend stays visible per attempt
            Timeout = callTimeout,
            Handlers = new[] { limitHandler }
        });

        return new LimitTranslatingChatClient(client.AsIChatClient(modelName, defaultMaxOutputTokens: null), Translate);
    }

    private static ModelLimitReachedException? Translate(Exception ex) => ex as ModelLimitReachedException;
}
