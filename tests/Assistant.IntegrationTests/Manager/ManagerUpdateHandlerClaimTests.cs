using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Infrastructure.Manager;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Manager;

public class ManagerUpdateHandlerClaimTests : IntegrationTestBase
{
    private sealed class FixedClaimCode : Assistant.Application.Manager.IClaimCodeProvider
    {
        public string Code => "424242";
    }

    private static readonly ReceivingBot ManagerBot = new(BotDbId: 1, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private ManagerUpdateHandler CreateHandler() => new(Db, new FixedClaimCode(), new SystemClock(), NullLogger<ManagerUpdateHandler>.Instance);

    private static IncomingUpdate ClaimCommand(long updateId, long userId, string? username, string args) =>
        new(updateId, new IncomingMessage(
            ChatId: userId, ChatType: "private", TopicId: null, MessageId: (int)updateId, UserId: userId, Username: username,
            Text: $"/claim {args}", Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}"));

    [Fact]
    public async Task Correct_code_creates_the_first_family_with_the_sender_as_owner()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "424242"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        var member = await Db.FamilyMembers.IgnoreQueryFilters().SingleAsync();
        member.TelegramUserId.ShouldBe(111);
        member.IsOwner.ShouldBeTrue();
        member.Status.ShouldBe(FamilyMemberStatus.Approved);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("Готово"));
    }

    [Fact]
    public async Task Wrong_code_creates_nothing()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "000000"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("Неверный"));
    }

    [Fact]
    public async Task Re_claiming_after_a_family_already_exists_is_rejected()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();
        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "424242"), CancellationToken.None);

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(2, 222, "test_other", "424242"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await Db.FamilyMembers.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("уже активирована"));
    }
}
