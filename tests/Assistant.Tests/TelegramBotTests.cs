using System.Net;
using System.Text;
using System.Text.Json;
using Assistant;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Assistant.Tests;

public sealed class TelegramBotTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid Subject = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private sealed class ModelHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Invalid { get; set; }
        public bool Unavailable { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var reading = Invalid ? "{\"value\":null,\"unit\":null}" : "{\"value\":7.8,\"unit\":\"mmol/L\"}";
            var body = JsonSerializer.Serialize(new { status = "completed", output = new[]
                { new { type = "message", content = new[] { new { type = "output_text", text = reading } } } } });
            return Task.FromResult(new HttpResponseMessage(Unavailable ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TelegramHandler : HttpMessageHandler
    {
        public List<string> Replies { get; } = [];
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            Replies.Add(payload);
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"ok\":true,\"result\":{}}", Encoding.UTF8, "application/json") };
        }
    }

    private static JsonElement Update(long user, long chat, long message, string text) =>
        JsonSerializer.SerializeToElement(new { message = new { from = new { id = user }, chat = new { id = chat },
            message_id = message, text } });

    private TelegramBot Create(HttpClient telegram, HttpClient model) => new(telegram, "synthetic-token", 111,
        "example_bot", new HashSet<long> { 101 }, new HashSet<long> { 501 }, Subject,
        new ReadingExtractor(model, "synthetic-key", "synthetic-model"), new ObservationStore(fixture.Options), fixture.Options,
        new RecipeGenerator(model, "synthetic-key", "synthetic-model"));

    private static (long MessageId, string Text) Reply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64(),
            doc.RootElement.GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task RecordIsDurableIdempotentAndLastReadsWithoutModel()
    {
        var modelHandler = new ModelHandler();
        var telegramHandler = new TelegramHandler();
        using var model = new HttpClient(modelHandler);
        using var telegram = new HttpClient(telegramHandler);
        var bot = Create(telegram, model);
        var update = Update(101, 501, 601, "/record 7.8 mmol/L");
        await bot.HandleAsync(update, CancellationToken.None);
        await bot.HandleAsync(update, CancellationToken.None);
        Assert.Equal(1, modelHandler.Calls);
        await using var db = new ObservationDb(fixture.Options);
        var rows = await db.Observations.AsNoTracking().Where(x => x.ChatId == 501 && x.MessageId == 601).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(Subject, row.SubjectId);
        Assert.Equal(111, row.BotId);
        Assert.Null(row.TopicId);
        Assert.Equal(7.8m, row.Value);
        Assert.Equal("mmol/L", row.Unit);
        Assert.Equal((601L, "Значение: 7.8 mmol/L\nИсточник: 111/501/601"), Reply(telegramHandler.Replies[0]));
        var restarted = Create(telegram, model);
        await restarted.HandleAsync(Update(101, 501, 602, "/last"), CancellationToken.None);
        Assert.Equal((602L, "Значение: 7.8 mmol/L\nИсточник: 111/501/601"), Reply(telegramHandler.Replies[^1]));
        Assert.Equal(1, modelHandler.Calls);
    }

    [Fact]
    public async Task UnauthorizedUserChatAndOtherBotHaveNoEffects()
    {
        var modelHandler = new ModelHandler();
        var telegramHandler = new TelegramHandler();
        using var model = new HttpClient(modelHandler);
        using var telegram = new HttpClient(telegramHandler);
        var bot = Create(telegram, model);
        await bot.HandleAsync(Update(102, 501, 611, "/record 7.8 mmol/L"), CancellationToken.None);
        await bot.HandleAsync(Update(101, 502, 612, "/record 7.8 mmol/L"), CancellationToken.None);
        await bot.HandleAsync(Update(101, 501, 613, "/record@otherbot 7.8 mmol/L"), CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(0, await db.Observations.CountAsync(x => x.MessageId >= 611 && x.MessageId <= 613));
        Assert.Equal(0, modelHandler.Calls);
        Assert.Empty(telegramHandler.Replies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReadingAndUnavailableProviderNeverStore(bool unavailable)
    {
        var modelHandler = new ModelHandler { Invalid = !unavailable, Unavailable = unavailable };
        var telegramHandler = new TelegramHandler();
        using var model = new HttpClient(modelHandler);
        using var telegram = new HttpClient(telegramHandler);
        var id = unavailable ? 622 : 621;
        await Create(telegram, model).HandleAsync(Update(101, 501, id, "/record sample"), CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(0, await db.Observations.CountAsync(x => x.ChatId == 501 && x.MessageId == id));
        Assert.Equal(unavailable ? "Сервис временно недоступен." : "Не удалось распознать значение и единицу.",
            Reply(Assert.Single(telegramHandler.Replies)).Text);
    }

    [Fact]
    public async Task FailedReplyAfterSaveCanBeRetriedWithoutDuplicateOrModelCall()
    {
        var modelHandler = new ModelHandler();
        var telegramHandler = new TelegramHandler { Fail = true };
        using var model = new HttpClient(modelHandler);
        using var telegram = new HttpClient(telegramHandler);
        var bot = Create(telegram, model);
        var update = Update(101, 501, 631, "/record 7.8 mmol/L");
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.HandleAsync(update, CancellationToken.None));
        telegramHandler.Fail = false;
        await bot.HandleAsync(update, CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.Observations.CountAsync(x => x.ChatId == 501 && x.MessageId == 631));
        Assert.Equal(1, modelHandler.Calls);
        Assert.Equal(631, Reply(Assert.Single(telegramHandler.Replies)).MessageId);
    }

    [Fact]
    public async Task DatabaseFailurePropagatesWithoutSuccessReply()
    {
        var modelHandler = new ModelHandler();
        var telegramHandler = new TelegramHandler();
        using var model = new HttpClient(modelHandler);
        using var telegram = new HttpClient(telegramHandler);
        var options = new DbContextOptionsBuilder<ObservationDb>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=synthetic;Username=synthetic;Password=synthetic;Timeout=1")
            .Options;
        var bot = new TelegramBot(telegram, "synthetic-token", 111, "example_bot",
            new HashSet<long> { 101 }, new HashSet<long> { 501 }, Subject,
            new ReadingExtractor(model, "synthetic-key", "synthetic-model"), new ObservationStore(options), options,
            new RecipeGenerator(model, "synthetic-key", "synthetic-model"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bot.HandleAsync(Update(101, 501, 641, "/record 7.8 mmol/L"), timeout.Token));

        Assert.IsType<NpgsqlException>(error.InnerException);
        Assert.Equal(0, modelHandler.Calls);
        Assert.Empty(telegramHandler.Replies);
    }
}
