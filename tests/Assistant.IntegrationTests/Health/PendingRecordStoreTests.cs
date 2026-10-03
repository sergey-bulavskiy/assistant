using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Health;

public class PendingRecordStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    private const string GlucosePayload = "{\"value\":10.0,\"context\":\"after_meal_1h\"}";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private readonly FixedClock _clock = new();

    private (AssistantDbContext Context, PendingRecordStore Store, EventStore Events) OpenScope(long? familyId)
    {
        var currentFamily = new CurrentFamily();
        if (familyId is not null)
        {
            currentFamily.Set(familyId);
        }

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        var context = new AssistantDbContext(options.Options, currentFamily);
        return (context, new PendingRecordStore(context, currentFamily, _clock), new EventStore(context, currentFamily, _clock));
    }

    private async Task<long> AddProfileAsync(long familyId, long botId)
    {
        var profile = new HealthProfile { FamilyId = familyId, BotId = botId, CreatedAt = Now, UpdatedAt = Now };
        Db.HealthProfiles.Add(profile);
        await Db.SaveChangesAsync();
        return profile.Id;
    }

    private static NewHealthEvent Glucose(DateTimeOffset at) => new("glucose", at, "message", GlucosePayload);

    private static NewPendingRecord Record(
        int telegramMessageId = 500, long userId = 111, int? topicId = 7, long chatId = -100, long botId = 1001, long? sourceMessageId = 40) =>
        new(sourceMessageId, botId, chatId, topicId, telegramMessageId, userId, new[] { Glucose(Now.AddMinutes(-5)) }, new[] { "glucose.any" });

    private async Task<long> AddAsync(long familyId, long profileId, NewPendingRecord record)
    {
        var (context, store, _) = OpenScope(familyId);
        await using (context)
        {
            return await store.AddAsync(familyId, profileId, record, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_row_round_trips_its_events_and_alerted_rules()
    {
        var profile = await AddProfileAsync(1, 10);

        var id = await AddAsync(1, profile, Record());

        var (context, store, _) = OpenScope(1);
        await using (context)
        {
            var info = (await store.FindAsync(1, id, CancellationToken.None)).ShouldNotBeNull();
            info.ProfileId.ShouldBe(profile);
            info.SourceMessageId.ShouldBe(40);
            info.BotId.ShouldBe(1001);
            info.ChatId.ShouldBe(-100);
            info.TopicId.ShouldBe(7);
            info.TelegramMessageId.ShouldBe(500);
            info.PromptMessageId.ShouldBeNull();
            info.RequestedByUserId.ShouldBe(111);
            info.Status.ShouldBe("pending");
            info.CreatedAt.ShouldBe(Now);
            info.AlertedRuleKeys.ShouldBe(new[] { "glucose.any" });
            var e = info.Events.ShouldHaveSingleItem();
            e.Type.ShouldBe("glucose");
            e.OccurredAt.ShouldBe(Now.AddMinutes(-5));
            e.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
            e.OccurredAtSource.ShouldBe("message");
            e.PayloadJson.ShouldBe(GlucosePayload);

            await store.SetPromptMessageAsync(1, id, 77, CancellationToken.None);
            (await store.FindAsync(1, id, CancellationToken.None))!.PromptMessageId.ShouldBe(77);
            context.ChangeTracker.Entries<PendingRecord>().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_pending_row_changes_status_exactly_once()
    {
        var profile = await AddProfileAsync(1, 10);
        var id = await AddAsync(1, profile, Record());
        _clock.UtcNow = Now.AddMinutes(3);

        var (context, store, _) = OpenScope(1);
        await using (context)
        {
            (await store.TryResolveAsync(1, id, "accepted", 222, null, CancellationToken.None)).ShouldBeTrue();
            (await store.TryResolveAsync(1, id, "accepted", 222, null, CancellationToken.None)).ShouldBeFalse();
            (await store.TryResolveAsync(1, id, "declined", 111, null, CancellationToken.None)).ShouldBeFalse();
        }

        var row = await Db.PendingRecords.AsNoTracking().SingleAsync();
        row.Status.ShouldBe("accepted");
        row.ResolvedByUserId.ShouldBe(222);
        row.ResolvedAt.ShouldBe(Now.AddMinutes(3));
    }

    [Fact]
    public async Task Work_inside_the_transaction_commits_with_the_status()
    {
        var profile = await AddProfileAsync(1, 10);
        var id = await AddAsync(1, profile, Record());

        var (context, store, events) = OpenScope(1);
        await using (context)
        {
            var source = new HealthEventSource(40, 1001, -100, 7, 111);
            var resolved = await store.TryResolveAsync(
                1, id, "accepted", 222,
                async ct => await events.AddAsync(1, profile, source, new[] { Glucose(Now.AddMinutes(-5)) }, ct),
                CancellationToken.None);
            resolved.ShouldBeTrue();
        }

        (await Db.Events.AsNoTracking().SingleAsync()).Payload.ShouldContain("10");
        (await Db.PendingRecords.AsNoTracking().SingleAsync()).Status.ShouldBe("accepted");
    }

    [Fact]
    public async Task A_failure_inside_the_transaction_keeps_the_row_pending()
    {
        var profile = await AddProfileAsync(1, 10);
        var id = await AddAsync(1, profile, Record());

        var (context, store, events) = OpenScope(1);
        await using (context)
        {
            var source = new HealthEventSource(40, 1001, -100, 7, 111);
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryResolveAsync(
                1, id, "accepted", 222,
                async ct =>
                {
                    await events.AddAsync(1, profile, source, new[] { Glucose(Now.AddMinutes(-5)) }, ct);
                    throw new InvalidOperationException("simulated failure after the save");
                },
                CancellationToken.None));

            // The same context still works and the row can be decided again.
            (await store.TryResolveAsync(1, id, "declined", 111, null, CancellationToken.None)).ShouldBeTrue();
        }

        (await Db.Events.CountAsync()).ShouldBe(0);
        (await Db.PendingRecords.AsNoTracking().SingleAsync()).Status.ShouldBe("declined");
    }

    [Fact]
    public async Task Pending_rows_are_found_by_the_original_or_the_button_message_only_while_pending()
    {
        var profile = await AddProfileAsync(1, 10);
        var first = await AddAsync(1, profile, Record(telegramMessageId: 500));
        await AddAsync(1, profile, Record(telegramMessageId: 600));
        await AddAsync(1, profile, Record(telegramMessageId: 500, chatId: -200));
        await AddAsync(1, profile, Record(telegramMessageId: 500, botId: 1002));

        var (context, store, _) = OpenScope(1);
        await using (context)
        {
            await store.SetPromptMessageAsync(1, first, 501, CancellationToken.None);

            (await store.FindPendingByTelegramMessageAsync(1, 1001, -100, 500, CancellationToken.None)).Select(p => p.Id).ShouldBe(new[] { first });
            (await store.FindPendingByTelegramMessageAsync(1, 1001, -100, 501, CancellationToken.None)).Select(p => p.Id).ShouldBe(new[] { first });

            await store.TryResolveAsync(1, first, "expired", null, null, CancellationToken.None);
            (await store.FindPendingByTelegramMessageAsync(1, 1001, -100, 500, CancellationToken.None)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task The_latest_pending_row_of_a_user_is_scoped_to_the_place_and_the_window()
    {
        var profile = await AddProfileAsync(1, 10);
        await AddAsync(1, profile, Record(telegramMessageId: 500));
        var newer = await AddAsync(1, profile, Record(telegramMessageId: 501));
        await AddAsync(1, profile, Record(telegramMessageId: 502, userId: 222));
        await AddAsync(1, profile, Record(telegramMessageId: 503, topicId: 8));

        var (context, store, _) = OpenScope(1);
        await using (context)
        {
            (await store.FindLatestPendingOfUserAsync(1, 1001, -100, 7, 111, Now.AddHours(-24), CancellationToken.None))!.Id.ShouldBe(newer);
            (await store.FindLatestPendingOfUserAsync(1, 1001, -100, 7, 111, Now.AddMinutes(1), CancellationToken.None)).ShouldBeNull();
            (await store.FindLatestPendingOfUserAsync(1, 1001, -100, null, 111, Now.AddHours(-24), CancellationToken.None)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Another_familys_rows_are_never_found_or_changed()
    {
        var profileA = await AddProfileAsync(1, 10);
        var idA = await AddAsync(1, profileA, Record());

        var (context, store, _) = OpenScope(2);
        await using (context)
        {
            (await store.FindAsync(2, idA, CancellationToken.None)).ShouldBeNull();
            (await store.FindPendingByTelegramMessageAsync(2, 1001, -100, 500, CancellationToken.None)).ShouldBeEmpty();
            (await store.TryResolveAsync(2, idA, "accepted", 333, null, CancellationToken.None)).ShouldBeFalse();
            await Should.ThrowAsync<InvalidOperationException>(() => store.AddAsync(2, profileA, Record(), CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => store.FindAsync(1, idA, CancellationToken.None));
        }

        (await Db.PendingRecords.AsNoTracking().SingleAsync()).Status.ShouldBe("pending");
    }

    [Fact]
    public async Task Every_method_fails_closed_without_a_family_scope()
    {
        var profile = await AddProfileAsync(1, 10);

        var (context, store, _) = OpenScope(null);
        await using (context)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => store.AddAsync(1, profile, Record(), CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => store.FindAsync(1, 1, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => store.SetPromptMessageAsync(1, 1, 5, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.FindPendingByTelegramMessageAsync(1, 1001, -100, 500, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.FindLatestPendingOfUserAsync(1, 1001, -100, 7, 111, Now, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.TryResolveAsync(1, 1, "accepted", 111, null, CancellationToken.None));
        }

        (await Db.PendingRecords.CountAsync()).ShouldBe(0);
    }
}
