using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Two_concurrent_first_messages_to_the_same_new_chat_create_only_one_place()
    {
        // Two independent DbContext instances on two independent connections, mirroring
        // ManagerUpdateHandlerClaimTests' concurrent-/claim test, so the two calls genuinely race
        // at the Postgres level (a single DbContext would just serialize them). TopicId is null on
        // both, which is the common case for a plain chat/DM and the case the places unique index's
        // NULLS NOT DISTINCT setting exists for.
        await SetupAsync();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });

        var optionsA = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(optionsA, ConnectionString);
        await using var dbA = new AssistantDbContext(optionsA.Options);
        var clientsA = new SingleClientFactory();
        var serviceA = new ApprovalService(dbA, clientsA, options, new SystemClock());

        var optionsB = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(optionsB, ConnectionString);
        await using var dbB = new AssistantDbContext(optionsB.Options);
        var clientsB = new SingleClientFactory();
        var serviceB = new ApprovalService(dbB, clientsB, options, new SystemClock());

        var results = await Task.WhenAll(
            serviceA.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None),
            serviceB.GetOrCreatePendingPlaceAsync(_botId, -100, null, "test chat", CancellationToken.None));

        results[0].ShouldBe(results[1]);
        (await Db.Places.CountAsync(p => p.BotId == _botId && p.ChatId == -100)).ShouldBe(1);
    }

    [Fact]
    public async Task Find_methods_read_a_status_without_creating_rows_or_sending_anything()
    {
        var (service, clients) = await SetupAsync();
        Db.Places.Add(new Place { BotId = _botId, ChatId = -100, TopicId = 7, Title = "test chat", Status = PlaceStatus.Approved, CreatedAt = DateTimeOffset.UtcNow });
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = _familyId, TelegramUserId = 333, DisplayName = "member three", Status = FamilyMemberStatus.Denied, IsOwner = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        (await service.FindPlaceStatusAsync(_botId, -100, 7, CancellationToken.None)).ShouldBe(PlaceStatus.Approved);
        (await service.FindPlaceStatusAsync(_botId, -100, null, CancellationToken.None)).ShouldBeNull();
        (await service.FindPlaceStatusAsync(_botId, -100, 8, CancellationToken.None)).ShouldBeNull();
        (await service.FindFamilyMemberStatusAsync(_familyId, 111, CancellationToken.None)).ShouldBe(FamilyMemberStatus.Approved);
        (await service.FindFamilyMemberStatusAsync(_familyId, 333, CancellationToken.None)).ShouldBe(FamilyMemberStatus.Denied);
        (await service.FindFamilyMemberStatusAsync(_familyId, 444, CancellationToken.None)).ShouldBeNull();
        (await service.FindFamilyMemberStatusAsync(_familyId + 1, 111, CancellationToken.None)).ShouldBeNull();

        (await Db.Places.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await Db.FamilyMembers.IgnoreQueryFilters().CountAsync()).ShouldBe(3);
        clients.Client.SentMessages.ShouldBeEmpty();
    }
}
