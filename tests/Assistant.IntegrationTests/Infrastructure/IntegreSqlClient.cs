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
        }
        else if (postResponse.StatusCode != HttpStatusCode.Locked)
        {
            throw new InvalidOperationException($"IntegreSQL template request failed: {postResponse.StatusCode}");
        }

        return await GetTestConnectionStringAsync(hash, cancellationToken);
    }

    public async Task<string> GetTestConnectionStringAsync(string hash, CancellationToken cancellationToken)
    {
        // When several test classes race to create the same template (same migration hash), the
        // POST above returns 423 Locked to everyone except the one initializing it. Until that
        // initializer finishes running migrations and PUTs the template ready, IntegreSQL answers
        // this endpoint with 503 Service Unavailable rather than blocking — so losers of the race
        // must poll briefly instead of treating 503 as a hard failure.
        const int maxAttempts = 60;
        var retryDelay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await _http.GetAsync($"api/v1/templates/{hash}/tests", cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var payload = await response.Content.ReadFromJsonAsync<TestResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("IntegreSQL returned an empty test database response.");
                return BuildConnectionString(payload.Database.Config);
            }

            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == maxAttempts)
            {
                throw new InvalidOperationException($"IntegreSQL test database request failed: {response.StatusCode}");
            }

            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new InvalidOperationException("IntegreSQL test database request failed: template never became ready.");
    }

    private string BuildConnectionString(DatabaseConfig config) =>
        $"Host={_pgHost};Port={_pgPort};Username={config.Username};Password={config.Password};Database={config.Database}";
}
