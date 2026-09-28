using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Families;

public class ApprovalServiceTests : IntegrationTestBase
{
    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();

        public ITelegramClient Create(string token) => Client;
    }

    private long _familyId;
    private long _botId;

    private async Task<(ApprovalService Service, SingleClientFactory Clients)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        _familyId = family.Id;

        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "owner one", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 222, DisplayName = "owner two", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 1001, Username = "test_role_bot", Role = "general", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        _botId = bot.Id;

        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var service = new ApprovalService(Db, clients, options, new SystemClock());
        return (service, clients);
    }

    [Fact]
    public async Task Creating_a_place_approval_DMs_every_owner_with_buttons()
    {
        var (service, clients) = await SetupAsync();

        var placeId = await service.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None);

        placeId.ShouldBeGreaterThan(0);
        clients.Client.SentMessages.Count.ShouldBe(2);
        clients.Client.SentMessages.ShouldContain(m => m.ChatId == 111);
        clients.Client.SentMessages.ShouldContain(m => m.ChatId == 222);
    }

    [Fact]
    public async Task Calling_it_again_for_the_same_key_does_not_send_a_second_round_of_DMs()
    {
        var (service, clients) = await SetupAsync();

        var first = await service.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None);
        var second = await service.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None);

        second.ShouldBe(first);
        clients.Client.SentMessages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task First_tap_wins_and_a_second_tap_is_a_no_op()
    {
        var (service, _) = await SetupAsync();
        var placeId = await service.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None);

        var first = await service.ResolvePlaceApprovalAsync(placeId, approve: true, CancellationToken.None);
        var second = await service.ResolvePlaceApprovalAsync(placeId, approve: false, CancellationToken.None);

        first.ShouldBe(ApprovalResolution.Applied);
        second.ShouldBe(ApprovalResolution.AlreadyResolved);
        (await Db.Places.FindAsync(placeId))!.Status.ShouldBe(PlaceStatus.Approved);
    }

    [Fact]
    public async Task User_approval_allow_marks_the_member_approved()
    {
        var (service, clients) = await SetupAsync();

        var memberId = await service.GetOrCreatePendingFamilyMemberAsync(_familyId, 333, "test user three", "test_user_three", "test_role_bot", CancellationToken.None);
        var result = await service.ResolveUserApprovalAsync(memberId, approve: true, CancellationToken.None);

        result.ShouldBe(ApprovalResolution.Applied);
        (await Db.FamilyMembers.FindAsync(memberId))!.Status.ShouldBe(FamilyMemberStatus.Approved);
        clients.Client.SentMessages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task User_approval_deny_marks_the_member_denied()
    {
        var (service, _) = await SetupAsync();

        var memberId = await service.GetOrCreatePendingFamilyMemberAsync(_familyId, 333, "test user three", "test_user_three", "test_role_bot", CancellationToken.None);
        await service.ResolveUserApprovalAsync(memberId, approve: false, CancellationToken.None);

        (await Db.FamilyMembers.FindAsync(memberId))!.Status.ShouldBe(FamilyMemberStatus.Denied);
    }
}
