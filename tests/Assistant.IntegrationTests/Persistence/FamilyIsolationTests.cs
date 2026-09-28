using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Persistence;

public class FamilyIsolationTests : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private long _familyAId;
    private long _familyBId;

    public async Task InitializeAsync()
    {
        _connectionString = await IntegreSqlPool.CreateTestDatabaseAsync();

        var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
        Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, _connectionString);
        await using var db = new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options);

        var familyA = new Family { Name = "test family A", CreatedAt = DateTimeOffset.UtcNow };
        var familyB = new Family { Name = "test family B", CreatedAt = DateTimeOffset.UtcNow };
        db.Families.AddRange(familyA, familyB);
        await db.SaveChangesAsync();
        _familyAId = familyA.Id;
        _familyBId = familyB.Id;

        db.FamilyMembers.Add(new FamilyMember { FamilyId = familyA.Id, TelegramUserId = 111, DisplayName = "test user", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.FamilyMembers.Add(new FamilyMember { FamilyId = familyB.Id, TelegramUserId = 222, DisplayName = "test user", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var botA = new Bot { FamilyId = familyA.Id, TelegramBotId = 1001, Username = "test_bot_a", Role = "general", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        var botB = new Bot { FamilyId = familyB.Id, TelegramBotId = 1002, Username = "test_bot_b", Role = "general", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        db.Bots.AddRange(botA, botB);
        await db.SaveChangesAsync();

        // Deliberately overlapping chat id, same as the spec's isolation-test requirement (§8).
        db.Places.Add(new Place { BotId = botA.Id, ChatId = -100, Title = "test chat", Status = PlaceStatus.Approved, CreatedAt = DateTimeOffset.UtcNow });
        db.Places.Add(new Place { BotId = botB.Id, ChatId = -100, Title = "test chat", Status = PlaceStatus.Approved, CreatedAt = DateTimeOffset.UtcNow });

        db.Messages.Add(new StoredMessage { BotId = botA.TelegramBotId, FamilyId = familyA.Id, ChatId = -100, TelegramMessageId = 1, ChatType = "group", Kind = MessageKind.Text, Text = "test message A", SentAt = DateTimeOffset.UtcNow, Raw = "{}", CreatedAt = DateTimeOffset.UtcNow });
        db.Messages.Add(new StoredMessage { BotId = botB.TelegramBotId, FamilyId = familyB.Id, ChatId = -100, TelegramMessageId = 1, ChatType = "group", Kind = MessageKind.Text, Text = "test message B", SentAt = DateTimeOffset.UtcNow, Raw = "{}", CreatedAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Assistant.Infrastructure.Persistence.AssistantDbContext> OpenScopedAsync(long familyId)
    {
        var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
        Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, _connectionString);
        var currentFamily = new CurrentFamily();
        currentFamily.Set(familyId);
        return await Task.FromResult(new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options, currentFamily));
    }

    [Fact]
    public async Task Family_A_scope_never_sees_family_B_rows()
    {
        await using var db = await OpenScopedAsync(_familyAId);

        (await db.FamilyMembers.CountAsync()).ShouldBe(1);
        (await db.FamilyMembers.SingleAsync()).TelegramUserId.ShouldBe(111);

        (await db.Bots.Where(b => b.FamilyId != null).CountAsync()).ShouldBe(1);
        (await db.Places.CountAsync()).ShouldBe(1);
        (await db.Messages.CountAsync()).ShouldBe(1);
        (await db.Messages.SingleAsync()).Text.ShouldBe("test message A");
    }

    [Fact]
    public async Task Family_B_scope_never_sees_family_A_rows()
    {
        await using var db = await OpenScopedAsync(_familyBId);

        (await db.FamilyMembers.CountAsync()).ShouldBe(1);
        (await db.FamilyMembers.SingleAsync()).TelegramUserId.ShouldBe(222);

        (await db.Bots.Where(b => b.FamilyId != null).CountAsync()).ShouldBe(1);
        (await db.Places.CountAsync()).ShouldBe(1);
        (await db.Messages.CountAsync()).ShouldBe(1);
        (await db.Messages.SingleAsync()).Text.ShouldBe("test message B");
    }

    [Fact]
    public async Task Unscoped_manager_context_sees_both_families_family_members_but_isolation_still_holds_per_scope()
    {
        await using var unscoped = await OpenScopedAsync(default);
        // default(long) is 0, not null — use the no-currentFamily constructor path instead to get
        // an actually-unfiltered (FamilyId == null) scope:
        var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
        Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, _connectionString);
        var currentFamily = new CurrentFamily();
        currentFamily.Set(null);
        await using var managerScoped = new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options, currentFamily);

        (await managerScoped.FamilyMembers.CountAsync()).ShouldBe(2);
    }
}
