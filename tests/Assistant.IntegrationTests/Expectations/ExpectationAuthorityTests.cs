using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Expectations;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Expectations;

public sealed class ExpectationAuthorityTests : ExpectationTestBase
{
    private sealed class Tokens : ITokenEncryptor
    { public byte[] Encrypt(string token) => [1]; public string Decrypt(byte[] cipher) => "synthetic-token"; }
    private sealed class Clients(FakeTelegramClient client) : ITelegramClientFactory
    { public ITelegramClient Create(string token) => client; }
    private static CallbackQueryInfo Callback(string action, long id) => new($"synthetic-{action}", 222,
        $"{action}:{id}", 222, 1);

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task Public_manager_place_revoke_retires_exact_topic_before_claim_and_reenable_does_not_revive(int? topic)
    {
        await SeedAsync(); var active = await ActiveAsync(HealthScope with { TopicId = topic });
        var unaffected = await ActiveAsync(HealthScope with { TopicId = 8 });
        await using var manager = Open(null);
        await manager.Db.FamilyMembers.IgnoreQueryFilters().Where(x => x.FamilyId == 12 && x.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsOwner, false));
        var place = await manager.Db.Places.IgnoreQueryFilters().SingleAsync(x => x.BotId == 22 && x.ChatId == -100 && x.TopicId == topic);
        var client = new FakeTelegramClient(); using var services = new ServiceCollection().BuildServiceProvider();
        var coordinator = new BotPollingCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new Clients(client), new Tokens(),
            Options.Create(new BotOptions { ManagerToken = "synthetic-manager" }), PollingWorkerSettings.Default,
            new PollingHealth(), Clock, NullLoggerFactory.Instance);
        var settings = new SettingsCommandHandler(manager.Db, coordinator, Clock);
        try
        {
            await settings.HandleCallbackAsync(Callback("settingsplace_disable", place.Id), "settingsplace_disable", place.Id, client, default);
            var retired = await RowAsync(active.Id); retired.Status.ShouldBe("retired"); retired.LastOutcome.ShouldBe("suppressed-authorization");
            retired.DueAt.ShouldBeNull(); retired.NextDate.ShouldBeNull();
            (await RowAsync(unaffected.Id)).Status.ShouldBe("active");
            await settings.HandleCallbackAsync(Callback("settingsplace_enable", place.Id), "settingsplace_enable", place.Id, client, default);
            (await manager.Db.Places.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == place.Id)).Status.ShouldBe(PlaceStatus.Approved);
            (await RowAsync(active.Id)).Status.ShouldBe("retired");
            Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z"); await using var due = Open();
            (await due.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldBeNull();
            (await due.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
            var recreated = await CreateAsync(HealthScope with { TopicId = topic }); recreated.Result.ShouldBe("preview");
            recreated.Id.ShouldNotBe(active.Id);
            (await DraftRowAsync(recreated.DraftId!.Value)).EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
        }
        finally { await coordinator.StopAsync(default); }
    }

    [Fact]
    public async Task Public_manager_member_revoke_retires_creator_across_role_bots_and_retires_pending_edit()
    {
        await SeedAsync(); var health = await ActiveAsync(); var vet = await ActiveAsync(VetScope);
        var otherCreator = await ActiveAsync(HealthScope with { ActorUserId = 222 }, type: "insulin");
        Assistant.Application.Expectations.ExpectationIntake edit;
        await using (var session = Open())
            edit = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", health.Id, DeadlineMinute: 600, GraceMinutes: 0), default);
        await DeliverAsync(edit, 701); await using var manager = Open(null);
        await manager.Db.FamilyMembers.IgnoreQueryFilters().Where(x => x.FamilyId == 12 && x.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsOwner, false));
        var member = await manager.Db.FamilyMembers.IgnoreQueryFilters().SingleAsync(x => x.FamilyId == 11 && x.TelegramUserId == 111);
        var client = new FakeTelegramClient(); using var services = new ServiceCollection().BuildServiceProvider();
        var coordinator = new BotPollingCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new Clients(client), new Tokens(),
            Options.Create(new BotOptions { ManagerToken = "synthetic-manager" }), PollingWorkerSettings.Default,
            new PollingHealth(), Clock, NullLoggerFactory.Instance);
        var settings = new SettingsCommandHandler(manager.Db, coordinator, Clock);
        try
        {
            await settings.HandleCallbackAsync(Callback("member_disable", member.Id), "member_disable", member.Id, client, default);
            (await RowAsync(health.Id)).Status.ShouldBe("retired"); (await RowAsync(vet.Id)).Status.ShouldBe("retired");
            (await RowAsync(otherCreator.Id)).Status.ShouldBe("active");
            (await DraftRowAsync(edit.DraftId!.Value)).Status.ShouldBe("retired");
            await settings.HandleCallbackAsync(Callback("member_enable", member.Id), "member_enable", member.Id, client, default);
            (await manager.Db.FamilyMembers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == member.Id)).Status.ShouldBe(FamilyMemberStatus.Approved);
            (await RowAsync(health.Id)).Status.ShouldBe("retired"); (await RowAsync(vet.Id)).Status.ShouldBe("retired");
            await using var callback = Open();
            (await callback.Store.ResolveAsync(HealthScope, edit.DraftId.Value, 701, true, default)).ShouldBe("expired");
            Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
            (await callback.Dispatch.ClaimAsync(HealthBot, new("expectation", health.Id, Clock.UtcNow), default)).ShouldBeNull();
            (await callback.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        finally { await coordinator.StopAsync(default); }
    }
}
