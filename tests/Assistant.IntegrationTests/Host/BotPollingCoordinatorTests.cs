using System.Net;
using Assistant.Application.Telegram;
using Assistant.Host;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.IntegrationTests.Host;

[Collection(HostFactoryCollection.Name)]
public class BotPollingCoordinatorTests : IAsyncLifetime
{
    private TestDatabaseLease? _database;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await IntegreSqlPool.CreateTestDatabaseAsync();
        _connectionString = _database.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static async Task WaitUntilHealthyAsync(HttpClient client)
    {
        for (var i = 0; i < 400; i++)
        {
            var response = await client.GetAsync("/health");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("host did not become healthy in time");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("condition was not met in time");
    }

    [Fact]
    public async Task Manager_bot_row_is_created_at_startup_and_starts_polling()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, _connectionString);
        await using var db = new AssistantDbContext(options.Options);
        var managerBot = await db.Bots.IgnoreQueryFilters().SingleAsync(b => b.Role == "manager");

        managerBot.FamilyId.ShouldBeNull();
        managerBot.Status.ShouldBe(Assistant.Domain.Bots.BotStatus.Active);
    }

    [Fact]
    public async Task An_update_sent_to_the_manager_bot_advances_its_offset()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        // The manager bot's own command handling is built in later tasks — for now (this task) any
        // update just needs to be safely absorbed and its offset advanced exactly once, proving
        // the coordinator's single worker really is polling. A private-chat text message is enough.
        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, new IncomingMessage(
            ChatId: 111, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: 1, UserId: 111, Username: "test_user",
            Text: "hello", Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null)));

        await WaitForConditionAsync(() =>
        {
            var options = new DbContextOptionsBuilder<AssistantDbContext>();
            AssistantDbContext.Configure(options, _connectionString);
            using var db = new AssistantDbContext(options.Options);
            var managerBot = db.Bots.IgnoreQueryFilters().Single(b => b.Role == "manager");
            return managerBot.LastUpdateId == 1;
        });
    }

    [Fact]
    public async Task StopAsync_is_safe_to_call_more_than_once()
    {
        // The ASP.NET Core hosting layer does not guarantee IHostedService.StopAsync is invoked
        // exactly once (WebApplicationFactory's own teardown is one place that can call it twice).
        // A repeat call must be a no-op, not throw ObjectDisposedException from re-cancelling or
        // re-disposing an already-stopped worker's CancellationTokenSource.
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        var coordinator = factory.Services.GetRequiredService<BotPollingCoordinator>();

        await coordinator.StopAsync(CancellationToken.None);
        await Should.NotThrowAsync(() => coordinator.StopAsync(CancellationToken.None));
    }

    private AssistantDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, _connectionString);
        return new AssistantDbContext(options.Options);
    }

    private async Task SetManagerOffsetAsync(long lastUpdateId, DateTimeOffset lastUpdateAt)
    {
        await using var db = OpenDb();
        await db.Bots.IgnoreQueryFilters().Where(b => b.Role == "manager").ExecuteUpdateAsync(s => s
            .SetProperty(b => b.LastUpdateId, lastUpdateId)
            .SetProperty(b => b.LastUpdateAt, lastUpdateAt));
    }

    private long ReadManagerOffset()
    {
        using var db = OpenDb();
        return db.Bots.IgnoreQueryFilters().AsNoTracking().Single(b => b.Role == "manager").LastUpdateId;
    }

    private static IncomingUpdate PrivateText(long updateId, int messageId) =>
        new(updateId, new IncomingMessage(
            ChatId: 111, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: messageId, UserId: 111, Username: "test_user",
            Text: "hello", Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null));

    [Fact]
    public async Task A_bot_idle_past_the_threshold_rebases_its_offset_and_accepts_a_lower_update_id()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        // After a week without updates Telegram picks the next update_id randomly, here below the
        // stored offset.
        await SetManagerOffsetAsync(1000, factory.Clock.UtcNow - BotPollingWorker.OffsetRebaseIdleThreshold - TimeSpan.FromHours(1));
        factory.TelegramClient.EnqueueUpdate(PrivateText(updateId: 50, messageId: 50));

        await WaitForConditionAsync(() => ReadManagerOffset() == 50);
        // The stored update refreshed the bot's activity, so polling continues from it instead of
        // re-basing again.
        await WaitForConditionAsync(() => factory.TelegramClient.RequestedOffsets[^1] == 51);
        var firstAt51 = factory.TelegramClient.RequestedOffsets.ToList().IndexOf(51);
        var pollsBefore = factory.TelegramClient.RequestedOffsets.Count;
        await WaitForConditionAsync(() => factory.TelegramClient.RequestedOffsets.Count >= pollsBefore + 5);

        factory.TelegramClient.RequestedOffsets.Skip(firstAt51).ShouldAllBe(offset => offset == 51);
    }

    [Fact]
    public async Task A_recently_active_bot_keeps_its_offset_and_never_fetches_older_update_ids()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        await SetManagerOffsetAsync(1000, factory.Clock.UtcNow - BotPollingWorker.OffsetRebaseIdleThreshold + TimeSpan.FromHours(1));
        await WaitForConditionAsync(() => factory.TelegramClient.RequestedOffsets[^1] == 1001);
        var pollsBefore = factory.TelegramClient.RequestedOffsets.Count;
        factory.TelegramClient.EnqueueUpdate(PrivateText(updateId: 50, messageId: 50));

        await WaitForConditionAsync(() => factory.TelegramClient.RequestedOffsets.Count >= pollsBefore + 5);

        factory.TelegramClient.RequestedOffsets.Skip(pollsBefore).ShouldAllBe(offset => offset == 1001);
        ReadManagerOffset().ShouldBe(1000);
    }
}
