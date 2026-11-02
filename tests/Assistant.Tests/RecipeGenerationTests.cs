using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Assistant.Tests;

public sealed class RecipeGenerationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid Subject = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static long nextChat = 600;
    private const string Preference = "Please avoid sesame in the recipe";
    private const string RecipeJson = "{\"title\":\"Суп\",\"ingredients\":[{\"name\":\"Вода\",\"grams\":250},{\"name\":\"Тыква\",\"grams\":125.5}],\"steps\":[\"Нарежьте тыкву.\",\"Сварите в воде.\"]}";

    private sealed class ModelHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Response { get; set; } = Completed(RecipeJson);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Timeout { get; set; }
        public Action? OnSend { get; set; }
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(token);
            OnSend?.Invoke();
            if (Timeout) throw new TaskCanceledException("synthetic timeout");
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TelegramHandler : HttpMessageHandler
    {
        public List<string> Replies { get; } = [];
        public bool Fail { get; set; }
        public long? MissingSourceId { get; set; }
        public List<(long Chat, long? Topic, long? ReplyTo, string Text)> Delivered { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var parameters = root.GetProperty("reply_parameters");
            var source = parameters.GetProperty("message_id").GetInt64();
            var missing = source == MissingSourceId;
            if (missing && (!parameters.TryGetProperty("allow_sending_without_reply", out var allow) || !allow.GetBoolean()))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var topic = root.TryGetProperty("message_thread_id", out var thread) ? thread.GetInt64() : (long?)null;
            Delivered.Add((root.GetProperty("chat_id").GetInt64(), topic, missing ? null : source,
                root.GetProperty("text").GetString()!));
            Replies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"ok\":true,\"result\":{}}", Encoding.UTF8, "application/json") };
        }
    }

    private static string Completed(string payload) => JsonSerializer.Serialize(new { status = "completed", output = new[]
        { new { type = "message", content = new[] { new { type = "output_text", text = payload } } } } });
    private static JsonElement Update(long chat, long id, string text, long? topic = null) =>
        JsonSerializer.SerializeToElement(new { message = new { from = new { id = 101 }, chat = new { id = chat },
            message_id = id, message_thread_id = topic, text } },
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    private static TelegramBot Bot(HttpClient telegram, HttpClient model, DbContextOptions<ObservationDb> options,
        long chat = 501, Guid? subject = null, long botId = 111) => new(telegram, "synthetic-token", botId, "example_bot",
            new HashSet<long> { 101 }, new HashSet<long> { chat }, subject ?? Subject,
            new ReadingExtractor(model, "synthetic-key", "synthetic-model"), new ObservationStore(options), options,
            new RecipeGenerator(model, "synthetic-key", "synthetic-model"));
    private static (long Id, string Text) Reply(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return (doc.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64(),
            doc.RootElement.GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task PersistedPreferenceReachesFreshHandlerAndSavedRecipeMatchesReply()
    {
        var tg = new TelegramHandler();
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await Bot(telegram, model, fixture.Options).HandleAsync(Update(501, 801, "/prefer " + Preference), CancellationToken.None);
        var restarted = Bot(telegram, model, fixture.Options);
        await restarted.HandleAsync(Update(501, 802, "/recipe"), CancellationToken.None);
        using var request = JsonDocument.Parse(api.RequestBody!);
        Assert.Equal(Preference, request.RootElement.GetProperty("input").GetString());
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        await using var db = new ObservationDb(fixture.Options);
        var row = await db.SavedRecipes.AsNoTracking().SingleAsync(x => x.BotId == 111 && x.ChatId == 501 && x.MessageId == 802);
        Assert.Equal(Subject, row.SubjectId);
        Assert.Null(row.TopicId);
        Assert.Equal("Суп", row.Title);
        Assert.Equal(new[] { new RecipeIngredient("Вода", 250m), new RecipeIngredient("Тыква", 125.5m) },
            JsonSerializer.Deserialize<RecipeIngredient[]>(row.IngredientsJson));
        Assert.Equal(new[] { "Нарежьте тыкву.", "Сварите в воде." }, JsonSerializer.Deserialize<string[]>(row.StepsJson));
        var expected = $"Рецепт #{row.Id}\nИсточник: 111/501/802\nСуп\nИнгредиенты:\n- Вода: 250 г\n- Тыква: 125.5 г\nПриготовление:\nНарежьте тыкву.\nСварите в воде.";
        Assert.Equal((802L, expected), Reply(tg.Replies[^1]));
        await restarted.HandleAsync(Update(501, 802, "/recipe"), CancellationToken.None);
        Assert.Equal((802L, expected), Reply(tg.Replies[^1]));
        Assert.Equal(1, api.Calls);
        Assert.Equal(1, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == 501 && x.MessageId == 802));
    }

    [Fact]
    public async Task ReplyFailureAfterSaveRedeliversSameRecipeWithoutProvider()
    {
        var tg = new TelegramHandler { Fail = true };
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111, ChatId = 503, MessageId = 810, Text = Preference }, CancellationToken.None);
        var bot = Bot(telegram, model, fixture.Options, 503);
        var update = Update(503, 811, "/recipe");
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.HandleAsync(update, CancellationToken.None));
        tg.Fail = false;
        await bot.HandleAsync(update, CancellationToken.None);
        await using var db = new ObservationDb(fixture.Options);
        var row = await db.SavedRecipes.AsNoTracking().SingleAsync(x => x.ChatId == 503 && x.MessageId == 811);
        Assert.Equal((811L, $"Рецепт #{row.Id}\nИсточник: 111/503/811\nСуп\nИнгредиенты:\n- Вода: 250 г\n- Тыква: 125.5 г\nПриготовление:\nНарежьте тыкву.\nСварите в воде."), Reply(Assert.Single(tg.Replies)));
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task DeletedSourceFallbackLetsPollingProcessNextUpdateWithoutRegenerating()
    {
        const long chat = 516;
        const long topic = 17;
        var tg = new TelegramHandler { MissingSourceId = 880 };
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        var store = new RecipeStore(fixture.Options);
        var saved = await store.SaveAsync(new SavedRecipe { SubjectId = Subject, BotId = 111, ChatId = chat,
            TopicId = topic, MessageId = 880, Title = "Saved", IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]",
            StepsJson = "[\"mix\"]" }, CancellationToken.None);
        var batch = JsonSerializer.SerializeToElement(new[] {
            new { update_id = 1001, message = Update(chat, 880, "/recipe", topic).GetProperty("message") },
            new { update_id = 1002, message = Update(chat, 881, "/shopping", topic).GetProperty("message") }
        });
        var handled = new List<long>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<JsonElement> Get(long offset, CancellationToken token) => Task.FromResult(batch);
        async Task Handle(JsonElement update, CancellationToken token)
        {
            await Bot(telegram, model, fixture.Options, chat).HandleAsync(update, token);
            var id = update.GetProperty("update_id").GetInt64();
            handled.Add(id);
            if (id == 1002) stop.Cancel();
        }

        await TelegramPolling.RunAsync(Get, Handle, TimeSpan.Zero, stop.Token);

        Assert.Equal(new long[] { 1001, 1002 }, handled);
        Assert.Equal(0, api.Calls);
        Assert.Equal((chat, (long?)topic, (long?)null,
            $"Рецепт #{saved.Id}\nИсточник: 111/516/880\nSaved\nИнгредиенты:\n- water: 10 г\nПриготовление:\nmix"), tg.Delivered[0]);
        Assert.Equal((chat, (long?)topic, (long?)881,
            $"Список покупок для рецепта #{saved.Id}\nИсточник: 111/516/880\n- water: 10 г"), tg.Delivered[1]);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 880));
    }

    [Fact]
    public async Task TimeoutAndFailedReplyAreRetriedByPollingWithoutSecondProviderAttempt()
    {
        const long chat = 514;
        var tg = new TelegramHandler { Fail = true };
        var api = new ModelHandler { Timeout = true };
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111,
            ChatId = chat, MessageId = 860, Text = Preference }, CancellationToken.None);
        var store = new RecipeStore(fixture.Options);
        var previous = await store.SaveAsync(new SavedRecipe { SubjectId = Subject, BotId = 111, ChatId = chat, MessageId = 861,
            Title = "Previous", IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]", StepsJson = "[\"mix\"]" }, CancellationToken.None);
        var message = Update(chat, 862, "/recipe").GetProperty("message");
        var updates = JsonSerializer.SerializeToElement(new[] { new { update_id = 1001, message } });
        var offsets = new List<long>();
        var deliveries = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<JsonElement> Get(long offset, CancellationToken token) { offsets.Add(offset); return Task.FromResult(updates); }
        async Task Handle(JsonElement update, CancellationToken token)
        {
            if (++deliveries == 2) tg.Fail = false;
            await Bot(telegram, model, fixture.Options, chat).HandleAsync(update, token);
            stop.Cancel();
        }

        await TelegramPolling.RunAsync(Get, Handle, TimeSpan.Zero, stop.Token);

        Assert.Equal(new long[] { 0, 0 }, offsets);
        Assert.Equal(1, api.Calls);
        Assert.Equal((862L, "Эта попытка уже обработана. Отправьте новую команду /recipe."), Reply(Assert.Single(tg.Replies)));
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.RecipeAttempts.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 862));
        Assert.Equal(0, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 862));
        Assert.Equal(previous.Id, (await store.LatestAsync(Subject, 111, chat, null, CancellationToken.None))!.Id);
        api.Timeout = false;
        await Bot(telegram, model, fixture.Options, chat).HandleAsync(Update(chat, 863, "/recipe"), CancellationToken.None);
        Assert.Equal(2, api.Calls);
        Assert.Equal(1, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 863));
    }

    [Fact]
    public async Task HostCancellationPropagatesAndRetainsAttemptReceipt()
    {
        const long chat = 515;
        var tg = new TelegramHandler();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var api = new ModelHandler { Timeout = true, OnSend = stop.Cancel };
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111,
            ChatId = chat, MessageId = 870, Text = Preference }, CancellationToken.None);
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Bot(telegram, model, fixture.Options, chat).HandleAsync(Update(chat, 871, "/recipe"), stop.Token));
        Assert.Equal(1, api.Calls);
        Assert.Empty(tg.Replies);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.RecipeAttempts.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 871));
        Assert.Equal(0, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 871));
    }

    [Fact]
    public async Task MissingOrDifferentScopePreferenceNeverCallsProvider()
    {
        var tg = new TelegramHandler();
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111, ChatId = 504, MessageId = 820, Text = Preference }, CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 504).HandleAsync(Update(504, 821, "/recipe", 2), CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 505).HandleAsync(Update(505, 822, "/recipe"), CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 504, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).HandleAsync(Update(504, 823, "/recipe"), CancellationToken.None);
        Assert.Equal(0, api.Calls);
        Assert.Equal(new[] { 821L, 822L, 823L }, tg.Replies.Select(x => Reply(x).Id));
        Assert.All(tg.Replies, x => Assert.Equal("Сначала сохраните предпочтение командой /prefer.", Reply(x).Text));
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(0, await db.SavedRecipes.CountAsync(x => x.MessageId >= 821 && x.MessageId <= 823));
    }

    [Fact]
    public async Task ConcurrentSameSourceWritesReturnOneStoredRecipe()
    {
        var store = new RecipeStore(fixture.Options);
        SavedRecipe Row(string title) => new() { SubjectId = Subject, BotId = 111, ChatId = 506, MessageId = 830,
            Title = title, IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]", StepsJson = "[\"mix\"]" };
        var result = await Task.WhenAll(store.SaveAsync(Row("first"), CancellationToken.None),
            store.SaveAsync(Row("second"), CancellationToken.None));
        await using var db = new ObservationDb(fixture.Options);
        var row = await db.SavedRecipes.AsNoTracking().SingleAsync(x => x.ChatId == 506 && x.MessageId == 830);
        Assert.Equal(row.Id, result[0].Id);
        Assert.Equal(row.Id, result[1].Id);
        Assert.Equal(row.Title, result[0].Title);
        Assert.Equal(row.Title, result[1].Title);
    }

    [Fact]
    public async Task DatabaseFailureDoesNotSendSuccessReply()
    {
        var tg = new TelegramHandler();
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        const long chat = 513;
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111,
            ChatId = chat, MessageId = 831, Text = Preference }, CancellationToken.None);
        var store = new RecipeStore(fixture.Options);
        var previous = await store.SaveAsync(new SavedRecipe { SubjectId = Subject, BotId = 111, ChatId = chat, MessageId = 832,
            Title = "Previous", IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]", StepsJson = "[\"mix\"]" }, CancellationToken.None);
        var payload = JsonSerializer.Serialize(new { title = "Blocked", ingredients = new[] { new { name = "water", grams = 10 } }, steps = new[] { "mix" } });
        api.Response = Completed(payload);
        await using var db = new ObservationDb(fixture.Options);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE saved_recipes ADD CONSTRAINT test_reject_recipe CHECK (\"Title\" <> 'Blocked')");
        try
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                Bot(telegram, model, fixture.Options, chat).HandleAsync(Update(chat, 833, "/recipe"), CancellationToken.None));
            Assert.Equal("23514", Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
            Assert.Equal(1, api.Calls);
            Assert.Empty(tg.Replies);
            var current = await store.LatestAsync(Subject, 111, chat, null, CancellationToken.None);
            Assert.Equal(previous.Id, current!.Id);
            Assert.Equal("Previous", current.Title);
            Assert.Equal(0, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == chat && x.MessageId == 833));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE saved_recipes DROP CONSTRAINT test_reject_recipe"); }
    }

    [Fact]
    public async Task ShoppingUsesNewestStoredRecipeEvenAfterEarlierSourceIsReplayed()
    {
        var tg = new TelegramHandler();
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        var store = new RecipeStore(fixture.Options);
        SavedRecipe Row(long message, string title, string ingredients) => new() { SubjectId = Subject, BotId = 111,
            ChatId = 507, MessageId = message, Title = title, IngredientsJson = ingredients, StepsJson = "[\"mix\"]" };
        var first = await store.SaveAsync(Row(840, "Old", "[{\"Name\":\"water\",\"Grams\":10}]"), CancellationToken.None);
        var second = await store.SaveAsync(Row(841, "New", "[{\"Name\":\"oats\",\"Grams\":80.5},{\"Name\":\"oats\",\"Grams\":12},{\"Name\":\"water\",\"Grams\":200}]"), CancellationToken.None);
        var bot = Bot(telegram, model, fixture.Options, 507);
        var expected = $"Список покупок для рецепта #{second.Id}\nИсточник: 111/507/841\n- oats: 80.5 г\n- oats: 12 г\n- water: 200 г";
        await bot.HandleAsync(Update(507, 842, "/shopping"), CancellationToken.None);
        Assert.Equal((842L, expected), Reply(tg.Replies[^1]));
        await bot.HandleAsync(Update(507, 840, "/recipe"), CancellationToken.None);
        Assert.Equal((840L, $"Рецепт #{first.Id}\nИсточник: 111/507/840\nOld\nИнгредиенты:\n- water: 10 г\nПриготовление:\nmix"), Reply(tg.Replies[^1]));
        await bot.HandleAsync(Update(507, 843, "/shopping"), CancellationToken.None);
        Assert.Equal((843L, expected), Reply(tg.Replies[^1]));
        Assert.Equal(0, api.Calls);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(2, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == 507));
    }

    [Fact]
    public async Task ShoppingDoesNotReadAnotherSubjectBotChatOrTopic()
    {
        var tg = new TelegramHandler();
        var api = new ModelHandler();
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new RecipeStore(fixture.Options).SaveAsync(new SavedRecipe { SubjectId = Subject, BotId = 111, ChatId = 508,
            MessageId = 850, Title = "Other scope", IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]", StepsJson = "[\"mix\"]" }, CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 508).HandleAsync(Update(508, 851, "/shopping", 4), CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 509).HandleAsync(Update(509, 852, "/shopping"), CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 508, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")).HandleAsync(Update(508, 853, "/shopping"), CancellationToken.None);
        await Bot(telegram, model, fixture.Options, 508, botId: 112).HandleAsync(Update(508, 854, "/shopping"), CancellationToken.None);
        Assert.Equal(new[] { 851L, 852L, 853L, 854L }, tg.Replies.Select(x => Reply(x).Id));
        Assert.All(tg.Replies, x => Assert.Equal("Сохранённых рецептов пока нет.", Reply(x).Text));
        Assert.Equal(0, api.Calls);
        await using var db = new ObservationDb(fixture.Options);
        Assert.Equal(1, await db.SavedRecipes.CountAsync(x => x.BotId == 111 && x.ChatId == 508));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"title\":\"x\",\"ingredients\":[],\"steps\":[\"x\"]}")]
    [InlineData("{\"title\":\"x\",\"ingredients\":[{\"name\":\"x\",\"grams\":1}],\"steps\":[]}")]
    [InlineData("{\"title\":\"x\",\"ingredients\":[{\"name\":\"x\",\"grams\":0}],\"steps\":[\"x\"]}")]
    [InlineData("{\"title\":\"x\",\"ingredients\":[{\"name\":\"x\",\"grams\":-1}],\"steps\":[\"x\"]}")]
    public async Task InvalidRecipeDoesNotReplacePriorSavedRecipe(string payload)
    {
        await AssertRejected(Completed(payload), HttpStatusCode.OK, "Не удалось составить рецепт. Отправьте новую команду /recipe.");
    }

    [Theory]
    [InlineData("{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"declined\"}]}]}")]
    public async Task IncompleteOrRefusedRecipeDoesNotReplacePriorSavedRecipe(string body) =>
        await AssertRejected(body, HttpStatusCode.OK, "Не удалось составить рецепт. Отправьте новую команду /recipe.");

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ProviderFailureDoesNotReplacePriorSavedRecipe(HttpStatusCode status) =>
        await AssertRejected("synthetic error", status, "Сервис временно недоступен. Отправьте новую команду /recipe.");

    [Fact]
    public async Task OversizeRecipeDoesNotReplacePriorSavedRecipe()
    {
        var payload = JsonSerializer.Serialize(new { title = new string('x', 4100), ingredients = new[] { new { name = "x", grams = 1 } }, steps = new[] { "x" } });
        await AssertRejected(Completed(payload), HttpStatusCode.OK, "Рецепт слишком длинный. Отправьте новую команду /recipe.");
    }

    private async Task AssertRejected(string body, HttpStatusCode status, string expectedReply)
    {
        var chat = Interlocked.Increment(ref nextChat);
        var tg = new TelegramHandler();
        var api = new ModelHandler { Response = body, Status = status };
        using var telegram = new HttpClient(tg);
        using var model = new HttpClient(api);
        await new PreferenceStore(fixture.Options).SaveAsync(new FoodPreference { SubjectId = Subject, BotId = 111, ChatId = chat, MessageId = 1, Text = Preference }, CancellationToken.None);
        var store = new RecipeStore(fixture.Options);
        var previous = await store.SaveAsync(new SavedRecipe { SubjectId = Subject, BotId = 111, ChatId = chat, MessageId = 2,
            Title = "Previous", IngredientsJson = "[{\"Name\":\"water\",\"Grams\":10}]", StepsJson = "[\"mix\"]" }, CancellationToken.None);
        await Bot(telegram, model, fixture.Options, chat).HandleAsync(Update(chat, 3, "/recipe"), CancellationToken.None);
        Assert.Equal((3L, expectedReply), Reply(Assert.Single(tg.Replies)));
        Assert.Equal(1, api.Calls);
        await using var db = new ObservationDb(fixture.Options);
        var stored = await db.SavedRecipes.AsNoTracking().Where(x => x.ChatId == chat).ToListAsync();
        Assert.Equal(previous.Id, Assert.Single(stored).Id);
        Assert.Equal(previous.Id, (await store.LatestAsync(Subject, 111, chat, null, CancellationToken.None))!.Id);
    }
}
