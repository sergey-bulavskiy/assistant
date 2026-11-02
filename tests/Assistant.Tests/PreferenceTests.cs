using System.Net;
using System.Text;
using System.Text.Json;
using Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Assistant.Tests;

public sealed class PreferenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid Subject = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class TelegramHandler : HttpMessageHandler
    {
        public List<string> Replies { get; } = [];
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            Replies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"ok\":true,\"result\":{}}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class UnexpectedModelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected provider call");
    }

    private static JsonElement Update(long user, long chat, long id, string text) =>
        JsonSerializer.SerializeToElement(new { message = new { from = new { id = user }, chat = new { id = chat },
            message_id = id, text } });

    private static TelegramBot Bot(HttpClient telegram, HttpClient model, DbContextOptions<ObservationDb> options) =>
        new(telegram, "synthetic-token", 111, "example_bot", new HashSet<long> { 101 }, new HashSet<long> { 501 },
            Subject, new ReadingExtractor(model, "synthetic-key", "synthetic-model"), new ObservationStore(options), options,
            new RecipeGenerator(model, "synthetic-key", "synthetic-model"));

    private static (long Id, string Text) Reply(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return (doc.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64(),
            doc.RootElement.GetProperty("text").GetString()!);
    }

    private static FoodPreference Row(Guid subject, long bot, long chat, long? topic, long message, string text) =>
        new() { SubjectId = subject, BotId = bot, ChatId = chat, TopicId = topic, MessageId = message, Text = text };

    [Fact]
    public async Task PreferencePersistsAndStaleRedeliveryDoesNotReplaceNewerValue()
    {
        var handler = new TelegramHandler();
        using var telegram = new HttpClient(handler);
        using var model = new HttpClient(new UnexpectedModelHandler());
        var bot = Bot(telegram, model, fixture.Options);
        const string first = "Please avoid sesame in the recipe";
        const string second = "Please use mushrooms";
        var original = Update(101, 501, 701, "/prefer " + first);
        await bot.HandleAsync(original, CancellationToken.None);
        await using (var db = new ObservationDb(fixture.Options))
        {
            var row = await db.FoodPreferences.AsNoTracking().SingleAsync(x => x.ChatId == 501 && x.MessageId == 701);
            Assert.Equal((Subject, 111L, 501L, (long?)null, 701L, first),
                (row.SubjectId, row.BotId, row.ChatId, row.TopicId, row.MessageId, row.Text));
        }
        Assert.Equal((701L, "Предпочтение сохранено: " + first), Reply(Assert.Single(handler.Replies)));
        Assert.Equal(first, (await new PreferenceStore(fixture.Options).LatestAsync(Subject, 111, 501, null, CancellationToken.None))!.Text);
        await Bot(telegram, model, fixture.Options).HandleAsync(Update(101, 501, 702, "/prefer " + second), CancellationToken.None);
        await bot.HandleAsync(original, CancellationToken.None);
        await using var fresh = new ObservationDb(fixture.Options);
        Assert.Equal(2, await fresh.FoodPreferences.CountAsync(x => x.ChatId == 501 && (x.MessageId == 701 || x.MessageId == 702)));
        Assert.Equal(second, (await new PreferenceStore(fixture.Options).LatestAsync(Subject, 111, 501, null, CancellationToken.None))!.Text);
        Assert.Equal((701L, "Эта команда уже обработана."), Reply(handler.Replies[^1]));
    }

    [Fact]
    public async Task FailedReplyAndConcurrentRedeliveryDoNotDuplicateSource()
    {
        var handler = new TelegramHandler { Fail = true };
        using var telegram = new HttpClient(handler);
        using var model = new HttpClient(new UnexpectedModelHandler());
        var bot = Bot(telegram, model, fixture.Options);
        var update = Update(101, 501, 711, "/prefer synthetic preference");
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.HandleAsync(update, CancellationToken.None));
        handler.Fail = false;
        await bot.HandleAsync(update, CancellationToken.None);
        Assert.Equal((711L, "Эта команда уже обработана."), Reply(Assert.Single(handler.Replies)));
        var store = new PreferenceStore(fixture.Options);
        var result = await Task.WhenAll(
            store.SaveAsync(Row(Subject, 111, 501, null, 712, "first"), CancellationToken.None),
            store.SaveAsync(Row(Subject, 111, 501, null, 712, "second"), CancellationToken.None));
        Assert.Single(result, x => x);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.FoodPreferences.CountAsync(x => x.BotId == 111 && x.ChatId == 501 && x.MessageId == 711));
        Assert.Equal(1, await db.FoodPreferences.CountAsync(x => x.BotId == 111 && x.ChatId == 501 && x.MessageId == 712));
    }

    [Fact]
    public async Task LatestUsesExactSubjectBotChatAndTopic()
    {
        var store = new PreferenceStore(fixture.Options);
        await store.SaveAsync(Row(Subject, 111, 510, null, 721, "original"), CancellationToken.None);
        await store.SaveAsync(Row(Guid.Parse("88888888-8888-8888-8888-888888888888"), 111, 510, null, 722, "other subject"), CancellationToken.None);
        await store.SaveAsync(Row(Subject, 112, 510, null, 723, "other bot"), CancellationToken.None);
        await store.SaveAsync(Row(Subject, 111, 511, null, 724, "other chat"), CancellationToken.None);
        await store.SaveAsync(Row(Subject, 111, 510, 3, 725, "other topic"), CancellationToken.None);
        Assert.Equal("original", (await new PreferenceStore(fixture.Options).LatestAsync(Subject, 111, 510, null, CancellationToken.None))!.Text);
        Assert.Equal("other topic", (await store.LatestAsync(Subject, 111, 510, 3, CancellationToken.None))!.Text);
    }

    [Fact]
    public async Task ValidationCountsUnicodeScalarsAndRejectsUnauthorizedRequests()
    {
        var handler = new TelegramHandler();
        using var telegram = new HttpClient(handler);
        using var model = new HttpClient(new UnexpectedModelHandler());
        var bot = Bot(telegram, model, fixture.Options);
        var valid = new string('a', 499) + "🍀";
        await bot.HandleAsync(Update(101, 501, 731, "/prefer"), CancellationToken.None);
        await bot.HandleAsync(Update(101, 501, 732, "/prefer   "), CancellationToken.None);
        await bot.HandleAsync(Update(101, 501, 733, "/prefer " + valid + "a"), CancellationToken.None);
        await bot.HandleAsync(Update(101, 501, 734, "/prefer " + valid), CancellationToken.None);
        await bot.HandleAsync(Update(102, 501, 735, "/prefer forbidden user"), CancellationToken.None);
        await bot.HandleAsync(Update(101, 502, 736, "/prefer forbidden chat"), CancellationToken.None);
        Assert.Equal(new[] { 731L, 732L, 733L, 734L }, handler.Replies.Select(x => Reply(x).Id));
        foreach (var reply in handler.Replies.Take(3)) Assert.Equal("Укажите предпочтение длиной до 500 символов.", Reply(reply).Text);
        Assert.Equal("Предпочтение сохранено: " + valid, Reply(handler.Replies[3]).Text);
        await using var db = new ObservationDb(fixture.Options);
        var rows = await db.FoodPreferences.AsNoTracking().Where(x => x.MessageId >= 731 && x.MessageId <= 736).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(734, row.MessageId);
        Assert.Equal(valid, row.Text);
    }

    [Fact]
    public async Task DatabaseFailureDoesNotSendSuccessReply()
    {
        var handler = new TelegramHandler();
        using var telegram = new HttpClient(handler);
        using var model = new HttpClient(new UnexpectedModelHandler());
        var store = new PreferenceStore(fixture.Options);
        Assert.True(await store.SaveAsync(Row(Subject, 111, 512, null, 741, "previous"), CancellationToken.None));
        var previous = await store.LatestAsync(Subject, 111, 512, null, CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE food_preferences ADD CONSTRAINT test_reject_preference CHECK (\"Text\" <> 'blocked')");
        try
        {
            var bot = new TelegramBot(telegram, "synthetic-token", 111, "example_bot", new HashSet<long> { 101 },
                new HashSet<long> { 512 }, Subject, new ReadingExtractor(model, "synthetic-key", "synthetic-model"),
                new ObservationStore(fixture.Options), fixture.Options, new RecipeGenerator(model, "synthetic-key", "synthetic-model"));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                bot.HandleAsync(Update(101, 512, 742, "/prefer blocked"), CancellationToken.None));
            Assert.Equal("23514", Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
            Assert.Empty(handler.Replies);
            var current = await store.LatestAsync(Subject, 111, 512, null, CancellationToken.None);
            Assert.Equal(previous!.Id, current!.Id);
            Assert.Equal("previous", current.Text);
            Assert.Equal(0, await db.FoodPreferences.CountAsync(x => x.BotId == 111 && x.ChatId == 512 && x.MessageId == 742));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE food_preferences DROP CONSTRAINT test_reject_preference"); }
    }
}
