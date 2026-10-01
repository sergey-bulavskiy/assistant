using System.Text.Json;
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
/// different SDK version's exception shape has one obvious place to translate from instead.
///
/// Review fix (B1/S1): classify by the error *body*, not by whether Retry-After is present (both a
/// genuine rate limit and a spend-cap hit return 429 `rate_limit_error`; per
/// https://platform.claude.com/docs/en/api/rate-limits#reaching-your-spend-cap a spend-cap 429's body
/// carries `error.details.error_code == "enforced_spend_limit_reached"` and no `retry-after` header,
/// while a workspace-set spend *limit* (as opposed to the tier's spend *cap*) is a 400
/// `invalid_request_error` whose message starts "You have reached your specified ... API usage
/// limits" (per https://platform.claude.com/docs/en/api/rate-limits#setting-your-own-spend-limit) --
/// an older, still-seen shape is a 400 `invalid_request_error` "Your credit balance is too low ...".
/// Both message shapes are matched case-insensitively ("usage limit" / "spend limit" / "credit
/// balance") so a future wording tweak doesn't silently stop being recognized. The handler buffers the
/// body with `ReadAsStringAsync` and disposes the response before throwing (S1) -- the original code
/// never read or disposed the response at all.</summary>
public static class AnthropicChatClientFactory
{
    // Review fix (S4): give the SDK a longer timeout than the gateway's own call timeout so the
    // gateway's `CancelAfter(callTimeout)` (LlmGateway.cs) always wins the race and produces the
    // user-facing timeout outcome, not the SDK. Task 9's DI wiring must set the shared
    // `HttpClient.Timeout` to at least `callTimeout + SdkTimeoutMargin` too (or
    // `Timeout.InfiniteTimeSpan` and rely on the SDK/gateway) -- see the M3b execution notes.
    private static readonly TimeSpan SdkTimeoutMargin = TimeSpan.FromSeconds(5);

    public static IChatClient Create(string apiKey, HttpClient httpClient, string modelName, TimeSpan callTimeout)
    {
        var limitHandler = Handler.Create(async (HttpRequestMessage request, NextHandler next, CancellationToken ct) =>
        {
            var response = await next.Invoke(request, ct);
            var status = (int)response.StatusCode;
            if (status != 429 && status != 400)
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var scope = ClassifyLimit(status, body);
            if (scope is null)
            {
                // An ordinary 400 (not a spend-limit message) -- rebuild an equivalent response from
                // the buffered body so the SDK can still parse it and throw its own typed exception.
                var replacement = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(body)
                };
                foreach (var header in response.Headers)
                {
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (response.Content.Headers.ContentType is { } contentType)
                {
                    replacement.Content.Headers.ContentType = contentType;
                }
                response.Dispose();
                return replacement;
            }

            DateTimeOffset? retryAt = scope == LlmLimitScope.Model
                ? RetryTimeParser.Parse(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null, DateTimeOffset.UtcNow)
                : null;

            response.Dispose(); // S1: never leak/throw with the response still open

            throw scope == LlmLimitScope.Model
                ? new ModelLimitReachedException($"Anthropic rate limit ({status})", LlmLimitScope.Model, retryAt)
                : new ModelLimitReachedException($"Anthropic usage/spend limit ({status})", LlmLimitScope.Provider, null);
        });

        var client = new AnthropicClient(new ClientOptions
        {
            HttpClient = httpClient,
            ApiKey = apiKey,
            MaxRetries = 0, // spec §10.5: fallback is immediate, spend stays visible per attempt
            Timeout = callTimeout + SdkTimeoutMargin,
            Handlers = new[] { limitHandler }
        });

        return new AuthorFoldingChatClient(new LimitTranslatingChatClient(client.AsIChatClient(modelName, defaultMaxOutputTokens: null), Translate));
    }

    private static ModelLimitReachedException? Translate(Exception ex) => ex as ModelLimitReachedException;

    // Review fix (B1): null means "not a limit error" (let the SDK build its own exception).
    private static LlmLimitScope? ClassifyLimit(int status, string body)
    {
        string? errorType = null;
        string? errorCode = null;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                // Review nit: GetString() throws InvalidOperationException for a non-string JSON
                // value -- an unexpected response shape (e.g. a provider returning "type": 123) must
                // fall through to the conservative per-status default below, not throw out of a
                // classification helper.
                errorType = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    errorCode = code.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable body: fall through to the conservative per-status default below.
        }

        if (status == 429)
        {
            if (errorType != "rate_limit_error")
            {
                return LlmLimitScope.Model; // unrecognized 429 shape -- treat as a model-scoped limit
            }

            return errorCode == "enforced_spend_limit_reached" || LooksLikeAccountLimitMessage(message)
                ? LlmLimitScope.Provider
                : LlmLimitScope.Model;
        }

        // status == 400: only a spend/usage-limit or low-credit-balance message counts as a limit;
        // every other 400 (e.g. malformed request) is not a limit at all.
        return LooksLikeAccountLimitMessage(message) ? LlmLimitScope.Provider : null;
    }

    private static bool LooksLikeAccountLimitMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("spend limit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("credit balance", StringComparison.OrdinalIgnoreCase);
    }
}
