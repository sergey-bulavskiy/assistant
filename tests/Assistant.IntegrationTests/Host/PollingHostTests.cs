using System.Net;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.Host;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.IntegrationTests.Host;

public class PollingHostTests : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        // R7: obtain the test database through the shared IntegreSqlPool helper — no bootstrap/
        // template-hash code duplicated here.
        _connectionString = await IntegreSqlPool.CreateTestDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IncomingMessage PrivateText(int messageId, long userId, string text) =>
        new(
            ChatId: userId,
            ChatType: "private",
            TopicId: null,
            MessageId: messageId,
            UserId: userId,
            Username: "test_user",
            Text: text,
            Kind: MessageKind.Text,
            IsEdit: false,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{}");

    private static IncomingMessage GroupText(int messageId, long chatId, long userId, string text) =>
        new(
            ChatId: chatId,
            ChatType: "group",
            TopicId: null,
            MessageId: messageId,
            UserId: userId,
            Username: "test_user",
            Text: text,
            Kind: MessageKind.Text,
            IsEdit: false,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{}");

    private async Task<int> CountMessagesAsync()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, _connectionString);
        await using var db = new AssistantDbContext(options.Options);
        return await db.Messages.CountAsync();
    }

    // M8: raised from 5s — the previous budget was tight enough to occasionally flake under CI
    // load before the host finished migrating and started polling.
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

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutSeconds = 10) =>
        await WaitForConditionAsync(() => Task.FromResult(condition()), timeoutSeconds);

    // M8: an async condition so callers never need `.GetAwaiter().GetResult()` to bridge a Task
    // into this loop.
    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("condition was not met in time");
    }

    [Fact]
    public async Task Private_message_is_stored_and_acknowledged()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));

        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.Text.StartsWith("Получил")));

        (await CountMessagesAsync()).ShouldBe(1);
        factory.TelegramClient.SentMessages.ShouldContain(m => m.Text == "Получил ✅ #1");
    }

    [Fact]
    public async Task Group_message_is_stored_without_a_reply()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, GroupText(1, -100, 111, "тест 1")));

        await WaitForConditionAsync(async () => await CountMessagesAsync() == 1);

        factory.TelegramClient.SentMessages.ShouldNotContain(m => m.ChatId == -100);
    }

    [Fact]
    public async Task Non_allowed_user_message_is_not_stored()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        // R10(a): no "sleep then assert nothing happened" — enqueue the non-allowed update
        // together with a later allowed sentinel update, and wait for the sentinel (not a fixed
        // delay) before asserting the non-allowed message was never stored or replied to.
        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 999999, "тест 1")));
        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(2, PrivateText(2, 111, "тест 2")));

        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.Text.StartsWith("Получил")));

        (await CountMessagesAsync()).ShouldBe(1);
        factory.TelegramClient.SentMessages.ShouldNotContain(m => m.ChatId == 999999);
        factory.TelegramClient.SentMessages.ShouldContain(m => m.Text == "Получил ✅ #1");
    }

    [Fact]
    public async Task Owner_receives_a_startup_notification()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.ChatId == 111 && m.Text.Contains("Запущен")));
    }

    [Fact]
    public async Task Health_endpoint_never_fails_the_request_and_becomes_healthy()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        await WaitUntilHealthyAsync(client);
    }

    [Fact]
    public async Task Restart_with_the_same_update_re_served_does_not_duplicate_or_reply_twice()
    {
        using (var factory1 = new AssistantWebApplicationFactory(_connectionString))
        {
            var client1 = factory1.CreateClient();
            await WaitUntilHealthyAsync(client1);

            factory1.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));
            await WaitForConditionAsync(() => factory1.TelegramClient.SentMessages.Any(m => m.Text == "Получил ✅ #1"));
        }

        (await CountMessagesAsync()).ShouldBe(1);

        // R10(b): the offset persisted to the database has moved past update 1. A fresh host
        // (factory2, fresh in-memory FakeTelegramClient) simulates Telegram redelivering that same
        // update anyway (IgnoreOffset), alongside a later sentinel update. We wait for the sentinel
        // — never a fixed sleep — before asserting the redelivered update did not create a
        // duplicate row or a second reply.
        using var factory2 = new AssistantWebApplicationFactory(_connectionString);
        factory2.TelegramClient.IgnoreOffset = true;
        factory2.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));
        factory2.TelegramClient.EnqueueUpdate(new IncomingUpdate(2, PrivateText(2, 111, "тест 2")));

        var client2 = factory2.CreateClient();
        await WaitUntilHealthyAsync(client2);

        await WaitForConditionAsync(() => factory2.TelegramClient.SentMessages.Any(m => m.Text == "Получил ✅ #2"));

        (await CountMessagesAsync()).ShouldBe(2);
        factory2.TelegramClient.SentMessages.ShouldNotContain(m => m.Text == "Получил ✅ #1");
    }

    [Fact]
    public async Task GetMe_failing_twice_then_succeeding_still_processes_updates()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        factory.TelegramClient.FailGetMeTimes(2);
        var client = factory.CreateClient();

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));

        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.Text.StartsWith("Получил")), timeoutSeconds: 15);

        (await CountMessagesAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Transient_EnsureBotState_failure_does_not_stop_the_host()
    {
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        factory.EnsureBotStateFailures.FailNextTimes(2);
        var client = factory.CreateClient();

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));

        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.Text.StartsWith("Получил")), timeoutSeconds: 15);

        (await CountMessagesAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Update_that_always_fails_is_skipped_after_the_poison_cap_and_health_recovers()
    {
        // I2(c): AssistantWebApplicationFactory's default PollingSettings uses a small
        // PoisonUpdateFailureCap (2) so this doesn't need many retries to observe the skip.
        using var factory = new AssistantWebApplicationFactory(_connectionString);
        factory.PoisonUpdate.AlwaysFailStoringUpdate(1);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));
        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(2, PrivateText(2, 111, "тест 2")));

        // Update 1 always fails to store; once it has failed PoisonUpdateFailureCap times in a
        // row, the host gives up on it (an offset-only store, no row ever inserted for it) and
        // moves on to update 2 in the same batch instead of retrying it forever. Update 2's
        // message ends up as the only (and therefore first) row, so its ack references db id 1 —
        // ReplyPolicy acks with the stored row's db id, not the Telegram update or message id.
        await WaitForConditionAsync(() => factory.TelegramClient.SentMessages.Any(m => m.Text.StartsWith("Получил")), timeoutSeconds: 15);

        factory.TelegramClient.SentMessages.Count(m => m.Text.StartsWith("Получил")).ShouldBe(1);
        (await CountMessagesAsync()).ShouldBe(1);

        // (a): once a whole batch is handled without an unrecovered failure, polling is marked
        // healthy again.
        await WaitUntilHealthyAsync(client);
    }

    [Fact]
    public async Task Update_that_keeps_failing_before_the_poison_cap_does_not_report_a_fresh_successful_poll()
    {
        // I2(b): a cap high enough that update 1 is never actually skipped within this test — every
        // retry must go through the normal exponential backoff, and no batch containing it ever
        // finishes handling.
        var pollingSettings = new PollingSettings(
            MinBackoff: TimeSpan.FromMilliseconds(20),
            MaxBackoff: TimeSpan.FromMilliseconds(50),
            LongPollTimeoutSeconds: 1,
            PoisonUpdateFailureCap: 1_000_000);

        using var factory = new AssistantWebApplicationFactory(_connectionString, pollingSettings);
        factory.PoisonUpdate.AlwaysFailStoringUpdate(1);
        var client = factory.CreateClient();
        await WaitUntilHealthyAsync(client);

        factory.TelegramClient.EnqueueUpdate(new IncomingUpdate(1, PrivateText(1, 111, "тест 1")));

        // Let several real failing poll/backoff cycles run (backoff sleeps use wall-clock time
        // regardless of the frozen offset TestClock applies below).
        await Task.Delay(300);

        // Deterministic check without waiting out PollingHealth's real multi-minute healthy
        // window: push the test clock's "now" forward past it instead. If a fresh success had
        // been marked since startup, "now" would still fall inside its window measured from that
        // success; since update 1 has been failing the whole time, the last recorded success is
        // still the pre-failure one, and this must report unhealthy.
        factory.Clock.Advance(TimeSpan.FromMinutes(3));

        var response = await client.GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
