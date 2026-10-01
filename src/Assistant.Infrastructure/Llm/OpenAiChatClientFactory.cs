using System.ClientModel;
using System.ClientModel.Primitives;
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
/// without the header present.</summary>
public static class OpenAiChatClientFactory
{
    public static IChatClient Create(string apiKey, HttpClient httpClient, string modelName, string? baseUrl, TimeSpan callTimeout)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = callTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0), // spec §10.5: MaxRetries = 0 on both SDKs
            Transport = new HttpClientPipelineTransport(httpClient)
        };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        var chatClient = client.GetChatClient(modelName).AsIChatClient();
        return new LimitTranslatingChatClient(chatClient, Translate);
    }

    private static ModelLimitReachedException? Translate(Exception ex)
    {
        if (ex is not ClientResultException resultException || resultException.Status != 429)
        {
            return null;
        }

        // Verified facts §B.4: a Retry-After header is documented as present on a real rate-limit
        // 429; its absence marks a spend/usage-limit condition instead (organization_spend_limit_exceeded
        // and friends, surfaced as e.g. "insufficient_quota" in the error body).
        var retryAfter = TryGetRetryAfter(resultException);
        return retryAfter is { } retryAt
            ? new ModelLimitReachedException("OpenAI rate limit (429, Retry-After present)", LlmLimitScope.Model, retryAt)
            : new ModelLimitReachedException("OpenAI quota/spend limit (429, no Retry-After)", LlmLimitScope.Provider, null);
    }

    private static DateTimeOffset? TryGetRetryAfter(ClientResultException ex)
    {
        var response = ex.GetRawResponse();
        if (response is not null &&
            response.Headers.TryGetValue("Retry-After", out var value) &&
            int.TryParse(value, out var seconds))
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        return null;
    }
}
