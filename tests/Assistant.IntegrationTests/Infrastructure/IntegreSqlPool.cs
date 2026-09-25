using System.Collections.Concurrent;
using System.Globalization;
using Assistant.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Assistant.IntegrationTests.Infrastructure;

public sealed record PoolContext(IntegreSqlClient Client);

/// <summary>
/// Boots (once per test process) a Postgres + IntegreSQL pair — either against externally provided
/// connection details (see <c>INTEGRESQL_URL</c>/<c>TEST_PG_HOST</c>/<c>TEST_PG_PORT</c>, used by the
/// docker-compose.tests.yml fast path) or via Testcontainers — and hands out fresh, migrated test
/// databases from the pool.
/// </summary>
public static class IntegreSqlPool
{
    private static readonly Lazy<Task<PoolContext>> LazyContext =
        new(InitializeAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    // Every test class in this assembly computes the exact same migration hash, so at suite
    // startup they all race to create/checkout against the same not-yet-existing template.
    // IntegreSQL (and the Postgres `CREATE DATABASE ... TEMPLATE` it issues under the hood) isn't
    // designed to queue many concurrent racers against a template that doesn't exist yet, and can
    // surface that as an intermittent 423/503/500 instead — so this process-local gate serializes
    // callers per hash: only one at a time runs the POST/migrate/PUT/GET dance, the rest simply
    // wait their turn, which (once the template is ready) is a fast no-op GET.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TemplateGates = new();

    public static Task<PoolContext> GetAsync() => LazyContext.Value;

    /// <summary>
    /// Shared, reusable helper: creates (or reuses) the "assistant schema" IntreSQL template — running
    /// EF Core migrations against it on first use — then checks out a fresh, isolated test database
    /// from the pool and returns its Npgsql connection string. Independent of <see cref="IntegrationTestBase"/>;
    /// callers that only need a raw connection string (e.g. host/WebApplicationFactory tests) can call this
    /// directly instead of spinning up their own containers or copying bootstrap code.
    /// </summary>
    public static async Task<string> CreateTestDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var pool = await GetAsync();

        var probeOptions = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(probeOptions, "Host=localhost;Database=probe;Username=postgres;Password=postgres");
        List<string> migrationIds;
        await using (var probe = new AssistantDbContext(probeOptions.Options))
        {
            migrationIds = probe.Database.GetMigrations().ToList();
        }

        var hash = TemplateHash.Compute(migrationIds);

        var gate = TemplateGates.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await pool.Client.GetOrCreateTemplateConnectionStringAsync(
                hash,
                async templateConnectionString =>
                {
                    var templateOptions = new DbContextOptionsBuilder<AssistantDbContext>();
                    AssistantDbContext.Configure(templateOptions, templateConnectionString);
                    await using (var templateDb = new AssistantDbContext(templateOptions.Options))
                    {
                        await templateDb.Database.MigrateAsync(cancellationToken);
                    }

                    // Npgsql keeps the physical connection to the template database alive in its
                    // client-side pool even after the DbContext is disposed. Postgres refuses
                    // `CREATE DATABASE ... TEMPLATE <template>` (what IntegreSQL does to hand out
                    // test databases) while any connection to the template database is open, so the
                    // pool must be cleared here before the template is marked ready (PUT below).
                    NpgsqlConnection.ClearAllPools();
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<PoolContext> InitializeAsync()
    {
        var integresqlUrl = Environment.GetEnvironmentVariable("INTEGRESQL_URL");
        var pgHost = Environment.GetEnvironmentVariable("TEST_PG_HOST");
        var pgPortRaw = Environment.GetEnvironmentVariable("TEST_PG_PORT");

        if (!string.IsNullOrEmpty(integresqlUrl) && !string.IsNullOrEmpty(pgHost) && !string.IsNullOrEmpty(pgPortRaw))
        {
            var http = new HttpClient { BaseAddress = new Uri(integresqlUrl) };
            var client = new IntegreSqlClient(http, pgHost, int.Parse(pgPortRaw, CultureInfo.InvariantCulture));
            return new PoolContext(client);
        }

        var network = new NetworkBuilder().Build();
        await network.CreateAsync();

        var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithNetwork(network)
            .WithNetworkAliases("postgres")
            .WithDatabase("assistant_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync();

        var integresql = new ContainerBuilder("ghcr.io/allaboutapps/integresql:v1.1.0")
            .WithNetwork(network)
            .WithNetworkAliases("integresql")
            .WithEnvironment("INTEGRESQL_PGHOST", "postgres")
            .WithEnvironment("INTEGRESQL_PGUSER", "postgres")
            .WithEnvironment("INTEGRESQL_PGPASSWORD", "postgres")
            // Defaults are runtime.NumCPU()-derived (see the IntegreSQL README's Configuration
            // section): on GitHub Actions' 2-core runners that's an initial pool of 2, a max of 8,
            // and only 2 pool-maintenance tasks running in parallel. With several test collections
            // (classes) able to run in parallel, each wanting a fresh database per test, a pool
            // that only ever has 2 warmed up forces most checkouts to wait on synchronous FIFO
            // recreation — which is exactly what surfaced as intermittent 423/503/500 responses on
            // CI. Fixed, CPU-independent values give the pool enough headroom to stay ahead of
            // checkout demand regardless of the runner's core count and regardless of how many
            // tests the suite grows to: a generous number warmed up immediately after the template
            // is finalized, a higher ceiling so returned databases have room to be recreated in the
            // background without blocking new checkouts, and more parallel maintenance tasks
            // (recreate/create) than the CI default — these are short, mostly I/O-bound
            // `CREATE/DROP DATABASE ... TEMPLATE` statements, not CPU-bound work, so
            // oversubscribing the 2 real vCPUs a little lets pool maintenance make forward progress
            // instead of queuing behind just 2 slots.
            .WithEnvironment("INTEGRESQL_TEST_INITIAL_POOL_SIZE", "15")
            .WithEnvironment("INTEGRESQL_TEST_MAX_POOL_SIZE", "30")
            .WithEnvironment("INTEGRESQL_POOL_MAX_PARALLEL_TASKS", "4")
            .WithPortBinding(5000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("http server started"))
            .Build();
        await integresql.StartAsync();

        var http2 = new HttpClient
        {
            BaseAddress = new Uri($"http://{integresql.Hostname}:{integresql.GetMappedPublicPort(5000)}/")
        };
        var client2 = new IntegreSqlClient(http2, postgres.Hostname, postgres.GetMappedPublicPort(5432));
        return new PoolContext(client2);

        // Testcontainers' Ryuk sidecar removes `postgres`, `integresql` and `network` when the test
        // process exits (or immediately, if it crashes) — no explicit Dispose call is needed here.
    }
}
