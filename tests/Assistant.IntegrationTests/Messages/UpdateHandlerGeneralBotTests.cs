using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Llm;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Messages;

/// <summary>The General assistant through UpdateHandler against the real message and chat-settings
/// stores: the persistence-dependent rules (outgoing replies as context, /new cutoff, /model per
/// chat and topic).</summary>
public class UpdateHandlerGeneralBotTests : IntegrationTestBase
{
    private const long UserId = 222;
    private const long GroupChatId = -100;

    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();

        public ITelegramClient Create(string token) => Client;
    }

    private sealed class NoopHealthAssistant : IHealthAssistant
    {
        public Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false) =>
            Task.CompletedTask;

        public Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopManagerUpdateHandler : IManagerUpdateHandler
    {
        public Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLlmGateway : ILlmGateway
    {
        public List<LlmRequest> Requests { get; } = new();

        public Queue<string> Answers { get; } = new();

        public bool IsEnabled => true;

        public IReadOnlyList<ModelStatus> DescribeModels() => new[] { new ModelStatus("sonnet", true, null), new ModelStatus("haiku", true, null) };

        public bool IsKnownModel(string name) => DescribeModels().Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(LlmResult.Answered(Answers.Count > 0 ? Answers.Dequeue() : "test answer", "sonnet"));
        }
    }

    private readonly RecordingLlmGateway _gateway = new();
    private long _nextUpdateId = 1;
    // Incoming ids start far above the fake client's sent-message ids (1, 2, ...): in one Telegram chat
    // message ids are unique across both directions, and messages has a unique (bot, chat, id) index.
    private int _nextMessageId = 10_000;

    private async Task<(UpdateHandler Handler, ReceivingBot Bot, FakeTelegramClient Telegram)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();

        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = UserId, DisplayName = "test user", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 1001, Username = "test_general_bot", Role = " General ", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();

        var receivingBot = new ReceivingBot(bot.Id, bot.TelegramBotId, bot.Username, family.Id, bot.Role);

        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clock = new SystemClock();
        var approvals = new ApprovalService(Db, clients, options, clock);
        var messageStore = new MessageStore(Db, clock, NullLogger<MessageStore>.Instance);
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var config = new LlmConfig
        {
            Models = new[] { new ModelCatalogEntry("test", "sonnet"), new ModelCatalogEntry("test", "haiku") },
            CallsPerMinute = 10,
            CallsPerDay = 100,
            MaxContextMessages = 50,
            MaxInputChars = 10_000,
            MaxOutputTokens = 1000,
            CallTimeoutSeconds = 60,
            MaxConcurrentCalls = 2,
            ModelCooldownMinutes = 5,
            Prices = new Dictionary<string, ModelPrice>(),
            Budget = null,
            FastModels = Array.Empty<ModelCatalogEntry>()
        };
        var generalAssistant = new GeneralAssistant(
            messageStore, _gateway, new ChatSettingsStore(Db, clock), new LlmUsageQuery(Db), config, clock, buildInfo, NullLogger<GeneralAssistant>.Instance);
        var handler = new UpdateHandler(
            messageStore, approvals, new CurrentFamily(), new NoopManagerUpdateHandler(), generalAssistant, new NoopHealthAssistant(), options, buildInfo, clock, NullLogger<UpdateHandler>.Instance);

        await messageStore.EnsureBotStateAsync(new BotIdentity(bot.TelegramBotId, bot.Username), CancellationToken.None);

        return (handler, receivingBot, clients.Client);
    }

    private IncomingMessage Text(string text, string chatType = "private", int? topicId = null) =>
        new(ChatId: chatType == "private" ? UserId : GroupChatId, ChatType: chatType, ChatTitle: chatType == "private" ? null : "test group",
            TopicId: topicId, MessageId: _nextMessageId++, UserId: UserId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: false, SentAt: DateTimeOffset.UtcNow, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    private Task SendAsync(UpdateHandler handler, ReceivingBot bot, FakeTelegramClient telegram, IncomingMessage message) =>
        handler.HandleAsync(bot, telegram, new IncomingUpdate(_nextUpdateId++, message), CancellationToken.None);

    private async Task ApproveGroupPlacesAsync()
    {
        foreach (var place in await Db.Places.IgnoreQueryFilters().ToListAsync())
        {
            place.Status = Assistant.Domain.Places.PlaceStatus.Approved;
        }

        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Answer_is_stored_as_an_outgoing_message_and_becomes_context_for_the_next_turn()
    {
        var (handler, bot, telegram) = await SetupAsync();
        _gateway.Answers.Enqueue("test first answer");

        await SendAsync(handler, bot, telegram, Text("test first question"));
        await SendAsync(handler, bot, telegram, Text("test second question"));

        var outgoing = await Db.Messages.IgnoreQueryFilters().Where(m => m.Direction == MessageDirection.Out).OrderBy(m => m.Id).ToListAsync();
        outgoing.Select(m => m.Text).ShouldBe(new[] { "test first answer", "test answer" });
        outgoing.ShouldAllBe(m => m.FamilyId == bot.FamilyId && m.UserId == null);

        _gateway.Requests.Count.ShouldBe(2);
        _gateway.Requests[1].Messages.Select(m => (m.Role, m.Text)).ShouldBe(new[]
        {
            (LlmMessageRole.User, "test first question"),
            (LlmMessageRole.Assistant, "test first answer"),
            (LlmMessageRole.User, "test second question"),
        });
    }

    [Fact]
    public async Task Long_answer_parts_are_each_stored()
    {
        var (handler, bot, telegram) = await SetupAsync();
        var firstParagraph = new string('a', 3000);
        var secondParagraph = new string('b', 3000);
        var expectedParts = new[] { firstParagraph, secondParagraph };
        _gateway.Answers.Enqueue(string.Join("\n\n", expectedParts));

        await SendAsync(handler, bot, telegram, Text("test question"));

        telegram.SentMessages.Select(m => m.Text).ShouldBe(expectedParts);
        var stored = await Db.Messages.IgnoreQueryFilters()
            .Where(m => m.Direction == MessageDirection.Out)
            .OrderBy(m => m.Id)
            .Select(m => m.Text)
            .ToListAsync();
        stored.ShouldBe(expectedParts);
    }

    [Fact]
    public async Task New_cutoff_drops_earlier_turns_from_the_next_requests_context()
    {
        var (handler, bot, telegram) = await SetupAsync();

        await SendAsync(handler, bot, telegram, Text("test old question"));
        await SendAsync(handler, bot, telegram, Text("/new"));
        await SendAsync(handler, bot, telegram, Text("test new question"));

        telegram.SentMessages.Select(m => m.Text).ShouldContain("Начинаем новый разговор.");
        var newCommand = await Db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Text == "/new");
        var setting = await Db.ChatSettings.IgnoreQueryFilters().SingleAsync();
        setting.ContextStartMessageId.ShouldBe(newCommand.Id);
        setting.BotId.ShouldBe(bot.TelegramBotId);

        _gateway.Requests.Last().Messages.Select(m => m.Text).ShouldBe(new[] { "test new question" });
    }

    [Fact]
    public async Task Model_preference_is_persisted_per_chat_and_topic()
    {
        var (handler, bot, telegram) = await SetupAsync();

        // First topic messages only create pending places; approve them, then talk.
        await SendAsync(handler, bot, telegram, Text("hello", "supergroup", topicId: 7));
        await SendAsync(handler, bot, telegram, Text("hello", "supergroup", topicId: 8));
        await ApproveGroupPlacesAsync();

        await SendAsync(handler, bot, telegram, Text("/model haiku", "supergroup", topicId: 7));
        await SendAsync(handler, bot, telegram, Text("@test_general_bot test question", "supergroup", topicId: 7));
        await SendAsync(handler, bot, telegram, Text("@test_general_bot test question", "supergroup", topicId: 8));

        var setting = await Db.ChatSettings.IgnoreQueryFilters().SingleAsync();
        setting.TopicId.ShouldBe(7);
        setting.PreferredModel.ShouldBe("haiku");
        _gateway.Requests.Select(r => r.PreferredModel).ShouldBe(new[] { "haiku", null });
    }

    [Fact]
    public async Task Reply_to_all_is_read_from_each_places_own_row_and_a_topic_never_inherits_it()
    {
        var (handler, bot, telegram) = await SetupAsync();

        // First messages only create the pending place rows: chat-wide, topic 7, topic 8.
        await SendAsync(handler, bot, telegram, Text("hello", "supergroup"));
        await SendAsync(handler, bot, telegram, Text("hello", "supergroup", topicId: 7));
        await SendAsync(handler, bot, telegram, Text("hello", "supergroup", topicId: 8));
        await ApproveGroupPlacesAsync();
        var places = await Db.Places.IgnoreQueryFilters().ToListAsync();
        places.Single(p => p.TopicId == null).ReplyToAll = true;
        places.Single(p => p.TopicId == 8).ReplyToAll = true;
        await Db.SaveChangesAsync();

        await SendAsync(handler, bot, telegram, Text("test chat question", "supergroup"));
        await SendAsync(handler, bot, telegram, Text("test topic 7 question", "supergroup", topicId: 7));
        await SendAsync(handler, bot, telegram, Text("test topic 8 question", "supergroup", topicId: 8));

        // Topic 7's own row is off even though the chat-wide row is on.
        _gateway.Requests.Select(r => r.Messages.Last().Text).ShouldBe(new[] { "test chat question", "test topic 8 question" });
    }
}
