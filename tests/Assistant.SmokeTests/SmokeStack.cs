using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Assistant.SmokeTests;

/// <summary>
/// The disposable stack under test: a fresh Postgres and the app container on one network, talking to
/// real Telegram through the smoke manager bot. Owns start, restart, claim-code lookup and DB access.
/// </summary>
public sealed class SmokeStack : IAsyncDisposable
{
    private static readonly Regex ClaimCodePattern = new("\"ClaimCode\":\"(\\d+)\"", RegexOptions.Compiled);

    private readonly INetwork _network;
    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _app;

    private SmokeStack(INetwork network, PostgreSqlContainer postgres, IContainer app, string encryptionKey)
    {
        _network = network;
        _postgres = postgres;
        _app = app;
        EncryptionKey = encryptionKey;
    }

    public string EncryptionKey { get; }

    public static async Task<SmokeStack> StartAsync(SmokeConfig config)
    {
        var imageName = config.Image ?? await BuildLocalImageAsync();
        var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var network = new NetworkBuilder().Build();
        await network.CreateAsync();

        var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithNetwork(network)
            .WithNetworkAliases("postgres")
            .WithDatabase("assistant")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync();

        var app = new ContainerBuilder(imageName)
            .WithNetwork(network)
            .WithEnvironment("TELEGRAM_MANAGER_BOT_TOKEN", config.ManagerToken)
            .WithEnvironment("TOKEN_ENCRYPTION_KEY", encryptionKey)
            .WithEnvironment("ConnectionStrings__Assistant", "Host=postgres;Port=5432;Database=assistant;Username=postgres;Password=postgres")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPath("/health").ForPort(8080)))
            .Build();
        await app.StartAsync();

        return new SmokeStack(network, postgres, app, encryptionKey);
    }

    /// <summary>Waits up to a minute for the one-time claim code the app logs while no family exists.</summary>
    public async Task<string> ReadClaimCodeAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            var (stdout, _) = await _app.GetLogsAsync();
            var match = ClaimCodePattern.Match(stdout);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The app did not log a claim code within a minute.");
    }

    /// <summary>Stops and starts the app container; the wait strategy returns once /health answers again. Postgres keeps its data.</summary>
    public async Task RestartAppAsync()
    {
        await _app.StopAsync();
        await _app.StartAsync();
    }

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(sql, parameters) ?? 0L, CultureInfo.InvariantCulture);

    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DisposeAsync();
    }

    // Rebuilt on every local run (Docker's layer cache keeps that fast) so the test never runs stale code.
    private static async Task<string> BuildLocalImageAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithName("assistant-smoke:local")
            .WithDockerfile("Dockerfile")
            .WithDockerfileDirectory(RepoRoot.Find())
            .Build();
        await image.CreateAsync();
        return image.FullName;
    }
}
