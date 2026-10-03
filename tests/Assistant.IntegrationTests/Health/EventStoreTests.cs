using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Health;

public class EventStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private (AssistantDbContext Context, EventStore Store) OpenScope(long? familyId, IClock? clock = null)
    {
        var currentFamily = new CurrentFamily();
        if (familyId is not null)
        {
            currentFamily.Set(familyId);
        }

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        var context = new AssistantDbContext(options.Options, currentFamily);
        return (context, new EventStore(context, currentFamily, clock ?? new FixedClock()));
    }

    private async Task<HealthProfile> AddProfileAsync(long familyId, long botId)
    {
        var profile = new HealthProfile { FamilyId = familyId, BotId = botId, CreatedAt = Now, UpdatedAt = Now };
        Db.HealthProfiles.Add(profile);
        await Db.SaveChangesAsync();
        return profile;
    }

    private async Task<long> AddMessageAsync(int telegramMessageId, long botId = 1001, long familyId = 1, long chatId = -100)
    {
        var message = new StoredMessage
        {
            BotId = botId,
            FamilyId = familyId,
            ChatId = chatId,
            TopicId = 7,
            TelegramMessageId = telegramMessageId,
            ChatType = "supergroup",
            Kind = MessageKind.Text,
            Text = "test message",
            SentAt = Now,
            Raw = "{}",
            CreatedAt = Now
        };
        Db.Messages.Add(message);
        await Db.SaveChangesAsync();
        return message.Id;
    }

    private static NewHealthEvent NewEvent(string type, string json, string at) =>
        new(type, DateTimeOffset.Parse(at), "stated", json);

    private static HealthEventSource Source(long messageDbId, long userId = 111, int? topicId = 7) =>
        new(messageDbId, 1001, -100, topicId, userId);

    private static NewHealthEvent Weight(string at = "2030-02-07T08:00:00Z") => NewEvent("weight", "{\"kg\":60}", at);

    private async Task<IReadOnlyList<HealthEventInfo>> AddAsync(
        long profileId, HealthEventSource source, params NewHealthEvent[] events)
    {
        var (context, store) = OpenScope(1);
        await using (context)
        {
            return await store.AddAsync(1, profileId, source, events, CancellationToken.None);
        }
    }

    private async Task<ReplacedEvents> ReplaceAsync(
        long profileId, HealthEventSource source, params NewHealthEvent[] events)
    {
        var (context, store) = OpenScope(1);
        await using (context)
        {
            return await store.ReplaceMessageEventsAsync(1, profileId, source, events, CancellationToken.None);
        }
    }

    private Task<List<HealthEvent>> RowsOfAsync(long messageId) =>
        Db.Events.AsNoTracking().Where(e => e.SourceMessageId == messageId).OrderBy(e => e.Id).ToListAsync();

    [Fact]
    public async Task Replace_keeps_unchanged_events_deletes_changed_ones_and_adds_new_ones()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var old = await AddAsync(
            profile.Id,
            Source(m1),
            NewEvent("glucose", "{\"value\":7.8,\"context\":\"other\"}", "2030-02-07T08:00:00Z"),
            Weight());

        var result = await ReplaceAsync(
            profile.Id,
            Source(m1),
            NewEvent("weight", "{\"kg\": 60.0}", "2030-02-07T08:00:00Z"),
            NewEvent("glucose", "{\"value\":2.5,\"context\":\"other\"}", "2030-02-07T08:00:00Z"),
            NewEvent("blood_pressure", "{\"systolic\":120,\"diastolic\":80,\"pulse\":null}", "2030-02-07T08:00:00Z"));

        result.Events.Count.ShouldBe(3);
        result.Events[0].Id.ShouldBe(old[1].Id);
        result.Events[1].Id.ShouldBeGreaterThan(old[1].Id);
        result.Events[2].Id.ShouldBeGreaterThan(old[1].Id);
        result.Events.Select(e => e.Type).ShouldBe(new[] { "weight", "glucose", "blood_pressure" });
        result.KeptCount.ShouldBe(1);
        result.Deleted.Single().Id.ShouldBe(old[0].Id);
        result.HadEvents.ShouldBeTrue();

        var rows = await RowsOfAsync(m1);
        rows.Count.ShouldBe(4);
        var oldGlucose = rows.Single(r => r.Id == old[0].Id);
        oldGlucose.DeletedAt.ShouldBe(Now);
        oldGlucose.DeleteReason.ShouldBe("edit");
        oldGlucose.UpdatedAt.ShouldBe(Now);
        rows.Where(r => r.Id != old[0].Id).ShouldAllBe(r => r.DeletedAt == null);
        rows.Where(r => r.Id > old[1].Id).ShouldAllBe(r => r.FamilyId == 1 && r.ProfileId == profile.Id && r.SubjectTag == "health"
            && r.BotId == 1001 && r.ChatId == -100 && r.TopicId == 7 && r.RecordedByUserId == 111
            && r.CreatedAt == Now && r.Flags.Length == 0);
        rows.Count(r => r.Id > old[1].Id).ShouldBe(2);
    }

    [Fact]
    public async Task Replace_with_the_same_events_changes_nothing()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var old = await AddAsync(
            profile.Id,
            Source(m1),
            NewEvent("glucose", "{\"value\":7.8,\"context\":\"other\"}", "2030-02-07T08:00:00Z"),
            Weight());

        var result = await ReplaceAsync(
            profile.Id,
            Source(m1),
            NewEvent("glucose", "{\"value\":7.8,\"context\":\"other\"}", "2030-02-07T08:00:00Z"),
            Weight());

        result.Events.Select(e => e.Id).ShouldBe(old.Select(e => e.Id));
        result.KeptCount.ShouldBe(2);
        result.Deleted.ShouldBeEmpty();
        var rows = await RowsOfAsync(m1);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.DeletedAt == null && r.UpdatedAt == Now);
    }

    [Fact]
    public async Task Replace_with_no_events_deletes_only_this_messages_events()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var m2 = await AddMessageAsync(2);
        var first = await AddAsync(profile.Id, Source(m1), Weight(), Weight("2030-02-07T09:00:00Z"));
        await AddAsync(profile.Id, Source(m2), Weight());

        var result = await ReplaceAsync(profile.Id, Source(m1));

        result.Events.ShouldBeEmpty();
        result.KeptCount.ShouldBe(0);
        result.Deleted.Select(e => e.Id).ShouldBe(first.Select(e => e.Id));
        result.HadEvents.ShouldBeTrue();
        (await RowsOfAsync(m1)).ShouldAllBe(r => r.DeleteReason == "edit");
        (await RowsOfAsync(m2)).ShouldAllBe(r => r.DeletedAt == null);
    }

    [Fact]
    public async Task Replace_matches_each_earlier_event_once()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var twins = await AddAsync(profile.Id, Source(m1), Weight(), Weight());

        var result = await ReplaceAsync(profile.Id, Source(m1), Weight());

        result.KeptCount.ShouldBe(1);
        result.Events.Single().Id.ShouldBe(twins[0].Id);
        result.Deleted.Single().Id.ShouldBe(twins[1].Id);
    }

    [Fact]
    public async Task Replace_ignores_deleted_events()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var gone = await AddAsync(profile.Id, Source(m1), Weight());
        var (context, store) = OpenScope(1);
        await using (context)
        {
            await store.DeleteByIdAsync(1, profile.Id, gone[0].Id, "del", CancellationToken.None);
        }

        var result = await ReplaceAsync(profile.Id, Source(m1), Weight());

        result.KeptCount.ShouldBe(0);
        result.Deleted.ShouldBeEmpty();
        result.HadEvents.ShouldBeFalse();
        result.Events.Single().Id.ShouldNotBe(gone[0].Id);
        (await Db.Events.AsNoTracking().SingleAsync(e => e.Id == gone[0].Id)).DeleteReason.ShouldBe("del");
    }

    [Fact]
    public async Task Replace_ignores_events_of_another_chat_with_the_same_message_id()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var otherChat = await AddAsync(profile.Id, new HealthEventSource(m1, 1001, -200, 7, 111), Weight());

        var result = await ReplaceAsync(profile.Id, Source(m1));

        result.Deleted.ShouldBeEmpty();
        (await Db.Events.AsNoTracking().SingleAsync(e => e.Id == otherChat[0].Id)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Replace_needs_the_source_message()
    {
        var profile = await AddProfileAsync(1, 10);
        var (context, store) = OpenScope(1);
        await using (context)
        {
            await Should.ThrowAsync<ArgumentException>(() => store.ReplaceMessageEventsAsync(
                1, profile.Id, new HealthEventSource(null, 1001, -100, 7, 111), new[] { Weight() }, CancellationToken.None));
        }

        (await Db.Events.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Add_saves_events_with_the_profile_subject_tag_and_source()
    {
        var profile = await AddProfileAsync(1, 10);
        var messageId = await AddMessageAsync(1);

        var infos = await AddAsync(
            profile.Id,
            Source(messageId),
            NewEvent("glucose", "{\"value\":7.8,\"context\":\"fasting\"}", "2030-02-07T08:00:00Z"),
            NewEvent("weight", "{\"kg\":64.5}", "2030-02-07T10:00:00Z"));

        infos.Count.ShouldBe(2);
        infos.ShouldAllBe(i => i.Id > 0);
        infos.Select(i => i.Type).ShouldBe(new[] { "glucose", "weight" });

        var rows = await Db.Events.OrderBy(e => e.Id).ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.FamilyId == 1 && r.ProfileId == profile.Id && r.SubjectTag == "health"
            && r.OccurredAtSource == "stated" && r.Flags.Length == 0 && r.SourceMessageId == messageId
            && r.BotId == 1001 && r.ChatId == -100 && r.TopicId == 7 && r.RecordedByUserId == 111
            && r.CreatedAt == Now && r.UpdatedAt == Now && r.DeletedAt == null);
        using var payload = JsonDocument.Parse(rows[0].Payload);
        payload.RootElement.GetProperty("value").GetDecimal().ShouldBe(7.8m);
        payload.RootElement.GetProperty("context").GetString().ShouldBe("fasting");
    }

    [Fact]
    public async Task Add_saves_the_safety_flags()
    {
        var profile = await AddProfileAsync(1, 10);
        var messageId = await AddMessageAsync(1);
        var flagged = NewEvent("glucose", "{\"value\":7.8,\"context\":\"after_meal_1h\"}", "2030-02-07T08:00:00Z")
            with { Flags = new[] { "out_of_target", "old_value_not_alerted" } };

        var infos = await AddAsync(profile.Id, Source(messageId), flagged, Weight());

        var rows = await Db.Events.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        rows.Select(r => r.Id).ShouldBe(infos.Select(i => i.Id));
        rows[0].Flags.ShouldBe(new[] { "out_of_target", "old_value_not_alerted" });
        rows[1].Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetActive_returns_the_range_oldest_first_without_deleted_events()
    {
        var profile = await AddProfileAsync(1, 10);
        var messageId = await AddMessageAsync(1);
        await AddAsync(profile.Id, Source(messageId), Weight("2030-02-06T23:00:00Z"));
        await AddAsync(profile.Id, Source(messageId), Weight("2030-02-07T08:00:00Z"));
        var nine = (await AddAsync(profile.Id, Source(messageId), Weight("2030-02-07T09:00:00Z"))).Single();
        await AddAsync(profile.Id, Source(messageId), Weight("2030-02-08T00:00:00Z"));
        await AddAsync(profile.Id, Source(messageId), Weight("2030-02-07T07:30:00Z"));

        var (context, store) = OpenScope(1);
        await using (context)
        {
            await store.DeleteByIdAsync(1, profile.Id, nine.Id, EventDeleteReasons.Del, CancellationToken.None);

            var active = await store.GetActiveAsync(
                1, profile.Id, DateTimeOffset.Parse("2030-02-07T00:00:00Z"), DateTimeOffset.Parse("2030-02-08T00:00:00Z"), CancellationToken.None);

            active.Select(a => a.OccurredAt).ShouldBe(new[]
            {
                DateTimeOffset.Parse("2030-02-07T07:30:00Z"),
                DateTimeOffset.Parse("2030-02-07T08:00:00Z")
            });
        }
    }

    [Fact]
    public async Task DeleteLatestOfUser_takes_the_newest_message_of_that_user_in_that_chat_and_topic()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var m2 = await AddMessageAsync(2);
        var m3 = await AddMessageAsync(3);
        var m4 = await AddMessageAsync(4);
        await AddAsync(profile.Id, Source(m1), Weight());
        var second = await AddAsync(profile.Id, Source(m2), Weight(), Weight("2030-02-07T09:00:00Z"));
        await AddAsync(profile.Id, Source(m3, userId: 222), Weight());
        await AddAsync(profile.Id, Source(m4, topicId: 8), Weight());

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var cutoff = DateTimeOffset.Parse("2030-02-06T10:00:00Z");

            var first = await store.DeleteLatestOfUserAsync(1, profile.Id, 1001, -100, 7, 111, cutoff, "undo", CancellationToken.None);
            first.Events.Select(e => e.Id).ShouldBe(second.Select(e => e.Id));
            first.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(-100, 2) });

            var deleted = await Db.Events.AsNoTracking().Where(e => e.SourceMessageId == m2).ToListAsync();
            deleted.ShouldAllBe(e => e.DeleteReason == "undo" && e.DeletedAt == Now);

            var next = await store.DeleteLatestOfUserAsync(1, profile.Id, 1001, -100, 7, 111, cutoff, "undo", CancellationToken.None);
            next.Events.Count.ShouldBe(1);
            next.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(-100, 1) });

            var none = await store.DeleteLatestOfUserAsync(1, profile.Id, 1001, -100, 7, 111, cutoff, "undo", CancellationToken.None);
            none.Events.ShouldBeEmpty();
        }

        // Another user's message and another topic stay active.
        (await Db.Events.AsNoTracking().Where(e => e.SourceMessageId == m3 || e.SourceMessageId == m4).ToListAsync())
            .ShouldAllBe(e => e.DeletedAt == null);
    }

    [Fact]
    public async Task DeleteLatestOfUser_ignores_events_created_before_the_cutoff()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var (oldContext, oldStore) = OpenScope(1, new FixedClock { UtcNow = DateTimeOffset.Parse("2030-02-06T09:00:00Z") });
        await using (oldContext)
        {
            await oldStore.AddAsync(1, profile.Id, Source(m1), new[] { Weight() }, CancellationToken.None);
        }

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var result = await store.DeleteLatestOfUserAsync(
                1, profile.Id, 1001, -100, 7, 111, DateTimeOffset.Parse("2030-02-06T10:00:00Z"), "undo", CancellationToken.None);

            result.Events.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task DeleteBySourceTelegramMessage_deletes_only_that_messages_events()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var m2 = await AddMessageAsync(2);
        await AddAsync(profile.Id, Source(m1), Weight(), Weight("2030-02-07T09:00:00Z"));
        await AddAsync(profile.Id, Source(m2), Weight());

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var result = await store.DeleteBySourceTelegramMessageAsync(1, profile.Id, 1001, -100, 1, "del", CancellationToken.None);
            result.Events.Count.ShouldBe(2);
            result.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(-100, 1) });

            (await Db.Events.AsNoTracking().Where(e => e.SourceMessageId == m1).ToListAsync())
                .ShouldAllBe(e => e.DeleteReason == "del" && e.DeletedAt != null);
            (await Db.Events.AsNoTracking().Where(e => e.SourceMessageId == m2).ToListAsync())
                .ShouldAllBe(e => e.DeletedAt == null);

            (await store.DeleteBySourceTelegramMessageAsync(1, profile.Id, 1001, -100, 1, "del", CancellationToken.None))
                .Events.ShouldBeEmpty();
            (await store.DeleteBySourceTelegramMessageAsync(1, profile.Id, 1002, -100, 2, "del", CancellationToken.None))
                .Events.ShouldBeEmpty();
            (await store.DeleteBySourceTelegramMessageAsync(1, profile.Id, 1001, -200, 2, "del", CancellationToken.None))
                .Events.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task DeleteBySourceTelegramMessage_ignores_events_created_before_the_cutoff()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var (oldContext, oldStore) = OpenScope(1, new FixedClock { UtcNow = DateTimeOffset.Parse("2030-02-06T09:00:00Z") });
        await using (oldContext)
        {
            await oldStore.AddAsync(1, profile.Id, Source(m1), new[] { Weight() }, CancellationToken.None);
        }

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var result = await store.DeleteBySourceTelegramMessageAsync(
                1, profile.Id, 1001, -100, 1, "undo", CancellationToken.None, DateTimeOffset.Parse("2030-02-06T10:00:00Z"));

            result.Events.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task DeleteById_reports_the_message_only_when_its_last_event_is_gone()
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);
        var added = await AddAsync(profile.Id, Source(m1), Weight(), Weight("2030-02-07T09:00:00Z"));

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var first = await store.DeleteByIdAsync(1, profile.Id, added[0].Id, "del", CancellationToken.None);
            first.Events.Count.ShouldBe(1);
            first.MessagesWithoutEvents.ShouldBeEmpty();

            var second = await store.DeleteByIdAsync(1, profile.Id, added[1].Id, "del", CancellationToken.None);
            second.Events.Count.ShouldBe(1);
            second.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(-100, 1) });

            (await store.DeleteByIdAsync(1, profile.Id, added[0].Id, "del", CancellationToken.None)).Events.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2L)]
    public async Task Every_method_throws_when_the_family_scope_is_unset_or_another_family(long? scopeFamilyId)
    {
        var profile = await AddProfileAsync(1, 10);
        var m1 = await AddMessageAsync(1);

        var (context, store) = OpenScope(scopeFamilyId);
        await using (context)
        {
            var from = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
            var to = DateTimeOffset.Parse("2031-01-01T00:00:00Z");

            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.AddAsync(1, profile.Id, Source(m1), new[] { Weight() }, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.GetActiveAsync(1, profile.Id, from, to, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.DeleteLatestOfUserAsync(1, profile.Id, 1001, -100, 7, 111, from, "undo", CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.DeleteBySourceTelegramMessageAsync(1, profile.Id, 1001, -100, 1, "del", CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.DeleteByIdAsync(1, profile.Id, 1, "del", CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.ReplaceMessageEventsAsync(1, profile.Id, Source(m1), new[] { Weight() }, CancellationToken.None));
        }

        (await Db.Events.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_family_scope_never_reads_or_deletes_another_familys_events()
    {
        var profileA = await AddProfileAsync(1, 10);
        var profileB = await AddProfileAsync(2, 20);
        var messageB = await AddMessageAsync(1, botId: 1002, familyId: 2);

        long eventB;
        var (contextB, storeB) = OpenScope(2);
        await using (contextB)
        {
            var added = await storeB.AddAsync(
                2, profileB.Id, new HealthEventSource(messageB, 1002, -100, 7, 222), new[] { Weight() }, CancellationToken.None);
            eventB = added.Single().Id;
        }

        var (context, store) = OpenScope(1);
        await using (context)
        {
            (await store.GetActiveAsync(
                1, profileB.Id, DateTimeOffset.Parse("2030-01-01T00:00:00Z"), DateTimeOffset.Parse("2031-01-01T00:00:00Z"), CancellationToken.None))
                .ShouldBeEmpty();
            (await store.DeleteByIdAsync(1, profileA.Id, eventB, "del", CancellationToken.None)).Events.ShouldBeEmpty();
            (await store.DeleteByIdAsync(1, profileB.Id, eventB, "del", CancellationToken.None)).Events.ShouldBeEmpty();
            await Should.ThrowAsync<InvalidOperationException>(() =>
                store.AddAsync(1, profileB.Id, Source(messageB), new[] { Weight() }, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => store.ReplaceMessageEventsAsync(
                1, profileB.Id, new HealthEventSource(messageB, 1002, -100, 7, 222), Array.Empty<NewHealthEvent>(), CancellationToken.None));
        }

        (await Db.Events.AsNoTracking().SingleAsync(e => e.Id == eventB)).DeletedAt.ShouldBeNull();
    }
}
