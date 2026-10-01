using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Builds an OpenAI IChatClient for one catalog entry (Decision 1: one client per entry,
/// model bound at construction via GetChatClient(model).AsIChatClient()).
///
/// Task 4 Step 0 confirmed Verified facts §B.2/§B.4 against the real 10.10.1/2.14.0 packages:
/// `OpenAIClientOptions.RetryPolicy` (typed `PipelinePolicy`) accepts a
/// `System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)` directly; `Transport` (typed
/// `PipelineTransport`) accepts `new HttpClientPipelineTransport(httpClient)`. Unlike the Anthropic
/// factory (Task 3), no capture-handler trick is needed here: `ClientResultException` exposes a
/// typed `GetRawResponse(): PipelineResponse` whose `Headers.TryGetValue("Retry-After", out value)`
/// reads the header directly -- confirmed empirically with a fake 429 response, both with and
/// without the header present.
///
/// Review fix (B1): classify by the error *body*, not by whether Retry-After is present --
/// `error.code`/`error.type` == "insufficient_quota" is a provider-wide spend/quota limit (no
/// meaningful retry time, even if the gateway happened to send one); any other 429 shape is a
/// model-scoped rate limit. `PipelineResponse.Content` (a `BinaryData`) carries the body.
///
/// Review fix (S4): `NetworkTimeout` is set to `callTimeout + 5s`, not `callTimeout` itself, so the
/// gateway's own `CancelAfter(callTimeout)` (LlmGateway.cs) always wins the race and the gateway, not
/// the SDK, produces the user-facing timeout outcome. Task 9's DI wiring must set the shared
/// `HttpClient.Timeout` to at least `callTimeout + 5s` too (or `Timeout.InfiniteTimeSpan` and rely on
/// the SDK/gateway) -- see the M3b execution notes.</summary>
public static class OpenAiChatClientFactory
{
    private static readonly TimeSpan SdkTimeoutMargin = TimeSpan.FromSeconds(5);

    public static IChatClient Create(string apiKey, HttpClient httpClient, string modelName, string? baseUrl, TimeSpan callTimeout)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = callTimeout + SdkTimeoutMargin,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0), // spec §10.5: MaxRetries = 0 on both SDKs
            Transport = new HttpClientPipelineTransport(httpClient)
        };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        var chatClient = client.GetChatClient(modelName).AsIChatClient();
        return new AuthorFoldingChatClient(new LimitTranslatingChatClient(chatClient, Translate));
    }

    private static ModelLimitReachedException? Translate(Exception ex)
    {
        if (ex is not ClientResultException resultException || resultException.Status != 429)
        {
            return null;
        }

        var response = resultException.GetRawResponse();
        var body = response?.Content?.ToString() ?? string.Empty;

        return IsInsufficientQuota(body)
            ? new ModelLimitReachedException("OpenAI quota/spend limit (429, insufficient_quota)", LlmLimitScope.Provider, null)
            : new ModelLimitReachedException(
                "OpenAI rate limit (429)",
                LlmLimitScope.Model,
                response is null ? null : RetryTimeParser.Parse(name => response.Headers.TryGetValue(name, out var v) ? v : null, DateTimeOffset.UtcNow));
    }

    // Review fix (B1): classify by the body's error code/type, not by Retry-After presence.
    private static bool IsInsufficientQuota(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            var type = error.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            return code == "insufficient_quota" || type == "insufficient_quota";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
