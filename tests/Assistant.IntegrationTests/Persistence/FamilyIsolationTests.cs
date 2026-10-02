using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Persistence;

public class FamilyIsolationTests : IAsyncLifetime
{
    private TestDatabaseLease? _database;
    private string _connectionString = string.Empty;
    private long _familyAId;
    private long _familyBId;

    public async Task InitializeAsync()
    {
        _database = await IntegreSqlPool.CreateTestDatabaseAsync();
        _connectionString = _database.ConnectionString;

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

        db.ChatSettings.Add(new ChatSetting { FamilyId = familyA.Id, BotId = botA.Id, ChatId = -100, TopicId = null, PreferredModel = "family A model", UpdatedAt = DateTimeOffset.UtcNow });
        db.ChatSettings.Add(new ChatSetting { FamilyId = familyB.Id, BotId = botB.Id, ChatId = -100, TopicId = null, PreferredModel = "family B model", UpdatedAt = DateTimeOffset.UtcNow });

        db.LlmCalls.Add(new LlmCall { FamilyId = familyA.Id, BotId = botA.Id, Tier = "cheap", Provider = "test", Model = "test model A", Outcome = LlmCallOutcome.Ok, DurationMs = 1, CreatedAt = DateTimeOffset.UtcNow });
        db.LlmCalls.Add(new LlmCall { FamilyId = familyB.Id, BotId = botB.Id, Tier = "cheap", Provider = "test", Model = "test model B", Outcome = LlmCallOutcome.Ok, DurationMs = 1, CreatedAt = DateTimeOffset.UtcNow });

        var profileA = new HealthProfile { FamilyId = familyA.Id, BotId = botA.Id, ContextNote = "family A note", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var profileB = new HealthProfile { FamilyId = familyB.Id, BotId = botB.Id, ContextNote = "family B note", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.HealthProfiles.AddRange(profileA, profileB);
        await db.SaveChangesAsync();

        db.SafetyRules.Add(new SafetyRule { FamilyId = familyA.Id, ProfileId = profileA.Id, RuleKey = "glucose.any", Source = "guideline_default", UpdatedAt = DateTimeOffset.UtcNow });
        db.SafetyRules.Add(new SafetyRule { FamilyId = familyB.Id, ProfileId = profileB.Id, RuleKey = "glucose.any", Source = "guideline_default", UpdatedAt = DateTimeOffset.UtcNow });
        db.Events.Add(new HealthEvent { FamilyId = familyA.Id, ProfileId = profileA.Id, Type = "weight", SubjectTag = "health", OccurredAt = DateTimeOffset.UtcNow, OccurredAtSource = "message", Payload = "{\"kg\":60}", BotId = botA.TelegramBotId, ChatId = -100, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.Events.Add(new HealthEvent { FamilyId = familyB.Id, ProfileId = profileB.Id, Type = "weight", SubjectTag = "health", OccurredAt = DateTimeOffset.UtcNow, OccurredAtSource = "message", Payload = "{\"kg\":60}", BotId = botB.TelegramBotId, ChatId = -100, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

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

        (await db.ChatSettings.CountAsync()).ShouldBe(1);
        (await db.ChatSettings.SingleAsync()).PreferredModel.ShouldBe("family A model");
        (await db.LlmCalls.CountAsync()).ShouldBe(1);
        (await db.LlmCalls.SingleAsync()).Model.ShouldBe("test model A");
        (await db.HealthProfiles.CountAsync()).ShouldBe(1);
        (await db.HealthProfiles.SingleAsync()).ContextNote.ShouldBe("family A note");
        (await db.SafetyRules.CountAsync()).ShouldBe(1);
        (await db.Events.CountAsync()).ShouldBe(1);
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

        (await db.ChatSettings.CountAsync()).ShouldBe(1);
        (await db.ChatSettings.SingleAsync()).PreferredModel.ShouldBe("family B model");
        (await db.LlmCalls.CountAsync()).ShouldBe(1);
        (await db.LlmCalls.SingleAsync()).Model.ShouldBe("test model B");
        (await db.HealthProfiles.CountAsync()).ShouldBe(1);
        (await db.HealthProfiles.SingleAsync()).ContextNote.ShouldBe("family B note");
        (await db.SafetyRules.CountAsync()).ShouldBe(1);
        (await db.Events.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unscoped_manager_context_sees_both_families_family_members_but_isolation_still_holds_per_scope()
    {
        var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
        Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, _connectionString);
        var currentFamily = new CurrentFamily();
        currentFamily.Set(null);
        await using var managerScoped = new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options, currentFamily);

        (await managerScoped.FamilyMembers.CountAsync()).ShouldBe(2);
        (await managerScoped.Bots.Where(b => b.FamilyId != null).CountAsync()).ShouldBe(2);
        (await managerScoped.HealthProfiles.CountAsync()).ShouldBe(2);
        (await managerScoped.Events.CountAsync()).ShouldBe(2);

        await using var familyAScoped = await OpenScopedAsync(_familyAId);
        (await familyAScoped.Places.CountAsync()).ShouldBe(1);
        (await familyAScoped.Messages.CountAsync()).ShouldBe(1);
    }
}
