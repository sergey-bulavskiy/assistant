using Assistant;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Assistant.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();
    public DbContextOptions<ObservationDb> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Options = new DbContextOptionsBuilder<ObservationDb>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ObservationDb(Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

public sealed class ObservationStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Observation Row(Guid subject, long chat, long message, long? topic, decimal value) => new()
    {
        SubjectId = subject, BotId = 111, ChatId = chat, MessageId = message,
        TopicId = topic, Value = value, Unit = "mmol/L"
    };

    [Fact]
    public async Task SavesExactSourceAndReadingInDatabase()
    {
        var subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = new ObservationStore(fixture.Options);
        await store.SaveAsync(Row(subject, 201, 301, null, 7.8m), CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        var row = await db.Observations.AsNoTracking().SingleAsync(x => x.ChatId == 201);
        Assert.Equal(subject, row.SubjectId);
        Assert.Equal(111, row.BotId);
        Assert.Equal(301, row.MessageId);
        Assert.Null(row.TopicId);
        Assert.Equal(7.8m, row.Value);
        Assert.Equal("mmol/L", row.Unit);
        Assert.Equal(row.Id, (await store.LatestAsync(subject, 111, 201, null, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task DuplicateSourceReturnsOriginalAndStoresOneRow()
    {
        var subject = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new ObservationStore(fixture.Options);
        var first = await store.SaveAsync(Row(subject, 202, 302, null, 5.1m), CancellationToken.None);
        var duplicate = await store.SaveAsync(Row(subject, 202, 302, null, 9.9m), CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(5.1m, duplicate.Value);
        Assert.Equal(1, await db.Observations.CountAsync(x => x.BotId == 111 && x.ChatId == 202 && x.MessageId == 302));
    }

    [Fact]
    public async Task LatestUsesSubjectBotChatAndExactTopicAndOrdersByInsertion()
    {
        var subject = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var other = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var store = new ObservationStore(fixture.Options);
        var first = await store.SaveAsync(Row(subject, 203, 303, null, 1m), CancellationToken.None);
        await store.SaveAsync(Row(other, 203, 304, null, 2m), CancellationToken.None);
        await store.SaveAsync(Row(subject, 204, 305, null, 3m), CancellationToken.None);
        await store.SaveAsync(Row(subject, 203, 306, 9, 4m), CancellationToken.None);
        var differentBot = Row(subject, 203, 307, null, 5m);
        differentBot.BotId = 112;
        await store.SaveAsync(differentBot, CancellationToken.None);
        Assert.Equal(first.Id, (await store.LatestAsync(subject, 111, 203, null, CancellationToken.None))!.Id);
        var last = await store.SaveAsync(Row(subject, 203, 308, null, 6m), CancellationToken.None);
        Assert.Equal(last.Id, (await store.LatestAsync(subject, 111, 203, null, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task ConcurrentDuplicateSourceStillStoresOneRow()
    {
        var subject = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var store = new ObservationStore(fixture.Options);
        var results = await Task.WhenAll(
            store.SaveAsync(Row(subject, 205, 309, null, 6.2m), CancellationToken.None),
            store.SaveAsync(Row(subject, 205, 309, null, 6.2m), CancellationToken.None));
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(1, await db.Observations.CountAsync(x => x.BotId == 111 && x.ChatId == 205 && x.MessageId == 309));
    }
}
