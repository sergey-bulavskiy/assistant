using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Assistant.IntegrationTests.Infrastructure;

public sealed record DatabaseConfig(string Host, int Port, string Username, string Password, string Database);

public sealed record TemplateDatabase(string TemplateHash, DatabaseConfig Config);

public sealed record TemplateResponse(TemplateDatabase Database);

public sealed record TestDatabase(DatabaseConfig Config);

public sealed record TestResponse(TestDatabase Database, int Id);

public sealed class IntegreSqlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Statuses IntegreSQL can return while it (or the Postgres it drives) is transiently
    // overloaded rather than genuinely broken: 423 Locked (another caller is already
    // initializing/holds a lock), 503 Service Unavailable (per the IntegreSQL README, "typically
    // a PostgreSQL connectivity problem" — e.g. Postgres briefly refusing connections under CPU
    // pressure), and 500 Internal Server Error (observed on CI's resource-limited 2-core runners
    // when pool bookkeeping — CREATE/DROP DATABASE ... TEMPLATE — can't keep up). None of these
    // indicate a permanent failure, so callers retry with a bounded exponential backoff instead of
    // failing the whole test run on the first blip.
    private static readonly HttpStatusCode[] TransientStatusCodes =
    [
        HttpStatusCode.Locked,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.InternalServerError,
    ];

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 12;

    private readonly HttpClient _http;
    private readonly string _pgHost;
    private readonly int _pgPort;

    public IntegreSqlClient(HttpClient http, string pgHost, int pgPort)
    {
        _http = http;
        _pgHost = pgHost;
        _pgPort = pgPort;
    }

    public async Task<string> GetOrCreateTemplateConnectionStringAsync(
        string hash, Func<string, Task> runMigrations, CancellationToken cancellationToken)
    {
        var delay = InitialRetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            var postResponse = await _http.PostAsJsonAsync("api/v1/templates", new { hash }, JsonOptions, cancellationToken);

            if (postResponse.StatusCode == HttpStatusCode.OK)
            {
                var payload = await postResponse.Content.ReadFromJsonAsync<TemplateResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("IntegreSQL returned an empty template response.");
                var connectionString = BuildConnectionString(payload.Database.Config);
                await runMigrations(connectionString);

                var putResponse = await _http.PutAsync($"api/v1/templates/{hash}", content: null, cancellationToken);
                if (putResponse.StatusCode != HttpStatusCode.NoContent)
                {
                    throw new InvalidOperationException($"IntegreSQL failed to finalize template {hash}: {putResponse.StatusCode}");
                }

                break;
            }

            if (postResponse.StatusCode == HttpStatusCode.Locked)
            {
                // Expected outcome of the race, not an error: another caller is already
                // initializing this template. Fall through to the GET below, which itself
                // tolerates the transient statuses IntegreSQL can return while that init runs.
                break;
            }

            if (!IsTransient(postResponse.StatusCode) || attempt == MaxAttempts)
            {
                throw new InvalidOperationException($"IntegreSQL template request failed: {postResponse.StatusCode}");
            }

            LogRetry("POST api/v1/templates", postResponse.StatusCode, attempt, delay);
            await Task.Delay(delay, cancellationToken);
            delay = NextDelay(delay);
        }

        return await GetTestConnectionStringAsync(hash, cancellationToken);
    }

    public async Task<string> GetTestConnectionStringAsync(string hash, CancellationToken cancellationToken)
    {
        // When several test classes race to create the same template (same migration hash), the
        // POST above returns 423 Locked to everyone except the one initializing it. Until that
        // initializer finishes running migrations and PUTs the template ready, IntegreSQL can
        // answer this endpoint with 423/503/500 rather than blocking — so losers of the race must
        // retry briefly (bounded exponential backoff) instead of treating any of those as a hard
        // failure.
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await _http.GetAsync($"api/v1/templates/{hash}/tests", cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var payload = await response.Content.ReadFromJsonAsync<TestResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("IntegreSQL returned an empty test database response.");
                return BuildConnectionString(payload.Database.Config);
            }

            if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
            {
                throw new InvalidOperationException($"IntegreSQL test database request failed: {response.StatusCode}");
            }

            LogRetry("GET api/v1/templates/{hash}/tests", response.StatusCode, attempt, delay);
            await Task.Delay(delay, cancellationToken);
            delay = NextDelay(delay);
        }

        throw new InvalidOperationException("IntegreSQL test database request failed: template never became ready.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) => Array.IndexOf(TransientStatusCodes, statusCode) >= 0;

    private static TimeSpan NextDelay(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, MaxRetryDelay.TotalMilliseconds));

    private static void LogRetry(string request, HttpStatusCode statusCode, int attempt, TimeSpan delay) =>
        Console.Error.WriteLine(
            $"IntegreSQL {request} returned {(int)statusCode} {statusCode} (attempt {attempt}/{MaxAttempts}); " +
            $"retrying in {delay.TotalMilliseconds:F0}ms.");

    private string BuildConnectionString(DatabaseConfig config) =>
        $"Host={_pgHost};Port={_pgPort};Username={config.Username};Password={config.Password};Database={config.Database}";
}
