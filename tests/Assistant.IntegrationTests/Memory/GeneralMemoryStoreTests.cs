using Assistant.Application.Common;
using Assistant.Application.Memory;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Memory;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Memory;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Memory;

public sealed class GeneralMemoryStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly GeneralMemoryScope Scope = new(11, 22, 999, -1001111111111, 7, 111, "supergroup");
    private readonly CurrentFamily _family = new();

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private GeneralMemoryStore Store(AssistantDbContext db) => new(db, _family, new Clock());
    private AssistantDbContext ScopedDb()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        return new(options.Options, _family);
    }
    private async Task SeedAsync()
    {
        Db.Families.Add(new Family { Id = 11, Name = "Synthetic family", CreatedAt = Now });
        Db.Bots.Add(new Bot { Id = 22, FamilyId = 11, TelegramBotId = 999, Username = "test_bot",
            Role = "general", Status = BotStatus.Active, CreatedAt = Now });
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = 11, TelegramUserId = 111, Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        Db.Places.AddRange(new Place { BotId = 22, ChatId = Scope.ChatId, TopicId = 7, Status = PlaceStatus.Approved, CreatedAt = Now },
            new Place { BotId = 22, ChatId = Scope.ChatId, TopicId = 8, Status = PlaceStatus.Approved, CreatedAt = Now });
        await Db.SaveChangesAsync();
        _family.Set(11);
    }
    private async Task<StoredMessage> MessageAsync(string text, int topic = 7, MessageDirection direction = MessageDirection.In)
    {
        var m = new StoredMessage { FamilyId = 11, BotId = 999, ChatId = Scope.ChatId, TopicId = topic,
            TelegramMessageId = 1000 + await Db.Messages.CountAsync(), UserId = 111, Direction = direction,
            Kind = MessageKind.Text, ChatType = "supergroup", Text = text, Raw = "{}", SentAt = Now, CreatedAt = Now };
        Db.Messages.Add(m);
        await Db.SaveChangesAsync();
        return m;
    }
    private async Task HistoryAsync(int count)
    {
        for (var i = 0; i < count; i++) await MessageAsync($"invented historical turn {i}");
    }

    [Fact]
    public async Task Russian_and_simple_search_respects_place_and_excludes_commands_and_outgoing()
    {
        await SeedAsync();
        var reading = await MessageAsync("синтетические измерения сохранены");
        var token = await MessageAsync("invented TOKENZETA");
        await MessageAsync("измерение TOKENZETA", 8);
        await MessageAsync("/remember TOKENZETA измерение");
        await MessageAsync("измерение TOKENZETA", direction: MessageDirection.Out);
        await using var db = ScopedDb();
        (await Store(db).SearchAsync(Scope, "измерение", default)).Select(x => x.MessageId).ShouldBe([reading.Id]);
        (await Store(db).SearchAsync(Scope, "tokenzeta", default)).Select(x => x.MessageId).ShouldBe([token.Id]);
    }

    [Fact]
    public async Task Search_orders_ties_latest_first_and_caps_ten()
    {
        await SeedAsync();
        var ids = new List<long>();
        for (var i = 0; i < 12; i++) ids.Add((await MessageAsync("same invented query")).Id);
        await using var db = ScopedDb();
        (await Store(db).SearchAsync(Scope, "invented", default)).Select(x => x.MessageId)
            .ShouldBe(ids.AsEnumerable().Reverse().Take(10));
    }

    [Fact]
    public async Task Remember_replay_keeps_one_fact_and_forget_does_not_cross_place()
    {
        await SeedAsync();
        var command = await MessageAsync("/remember invented saved fact");
        await using var db = ScopedDb();
        var store = Store(db);
        var first = await store.RememberAsync(Scope, command.Id, "invented saved fact", default);
        (await store.RememberAsync(Scope, command.Id, "invented saved fact", default)).FactId.ShouldBe(first.FactId);
        (await store.ReadAsync(Scope, default)).Facts.ShouldHaveSingleItem().Id.ShouldBe(first.FactId!.Value);
        (await store.ForgetAsync(Scope with { TopicId = 8 }, first.FactId!.Value, default)).ShouldBeFalse();
        (await store.ReadAsync(Scope, default)).Facts.ShouldHaveSingleItem();
        (await store.ForgetAsync(Scope, first.FactId.Value, default)).ShouldBeTrue();
        (await store.ReadAsync(Scope, default)).Facts.ShouldBeEmpty();
        (await db.Set<GeneralMemoryFact>().AsNoTracking().SingleAsync()).RetiredAt.ShouldBe(Now);
        var replay = await store.RememberAsync(Scope, command.Id, "invented saved fact", default);
        replay.FactId.ShouldBe(first.FactId);
        replay.IsRetired.ShouldBeTrue();
        (await store.ReadAsync(Scope, default)).Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remember_rejects_mismatched_source_or_oversized_input()
    {
        await SeedAsync();
        var command = await MessageAsync("/remember original");
        await using var db = ScopedDb();
        var store = Store(db);
        (await store.RememberAsync(Scope, command.Id, "different", default)).FactId.ShouldBeNull();
        (await store.RememberAsync(Scope, command.Id, new string('x', 501), default)).Failure.ShouldBe("invalid");
        (await store.ReadAsync(Scope, default)).Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task New_reset_excludes_search_and_summary_but_keeps_explicit_facts()
    {
        await SeedAsync();
        var command = await MessageAsync("/remember enduring fact");
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        await store.RememberAsync(Scope, command.Id, "enduring fact", default);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        (await store.CommitFoldAsync(Scope, fold!, "older summary", "test", default)).ShouldBeTrue();
        var reset = await MessageAsync("/new");
        await new ChatSettingsStore(db, new Clock()).SetContextStartMessageIdAsync(11, 999, Scope.ChatId, 7, reset.Id, default);
        (await store.SearchAsync(Scope, "historical", default)).ShouldBeEmpty();
        var after = await store.ReadAsync(Scope, default);
        after.Summary.ShouldBeNull();
        after.Facts.ShouldHaveSingleItem().Text.ShouldBe("enduring fact");
        (await store.CommitFoldAsync(Scope, fold!, "stale summary", "test", default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Fold_below_twenty_turns_older_than_recent_window_is_not_prepared()
    {
        await SeedAsync();
        await HistoryAsync(29);
        await using var db = ScopedDb();
        var fold = await Store(db).PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        fold.ShouldBeNull();
    }

    [Fact]
    public async Task Fold_at_twenty_turns_older_than_recent_window_includes_exact_twenty()
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var fold = await Store(db).PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        fold!.Sources.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Bounded_fold_persists_watermark_and_restart_advances_from_it()
    {
        await SeedAsync();
        await HistoryAsync(75);
        await using (var db = ScopedDb())
        {
            var fold = await Store(db).PrepareFoldAsync(Scope, long.MaxValue, 10, default);
            fold!.Sources.Count.ShouldBe(50);
            (await Store(db).CommitFoldAsync(Scope, fold, "first fifty turns", "test", default)).ShouldBeTrue();
        }
        await HistoryAsync(10);
        await using var resumed = ScopedDb();
        var snapshot = await Store(resumed).ReadAsync(Scope, default);
        snapshot.Summary!.Text.ShouldBe("first fifty turns");
        var next = await Store(resumed).PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        next!.Sources.Count.ShouldBe(25);
        next.Sources.All(x => x.Id > snapshot.Summary.ThroughMessageId).ShouldBeTrue();
        (await Store(resumed).CommitFoldAsync(Scope, next, "updated summary", "test", default)).ShouldBeTrue();
    }

    [Fact]
    public async Task Changed_selected_source_rejects_in_flight_fold()
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        await Db.Messages.Where(x => x.Id == fold!.Sources[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "changed source"));
        (await store.CommitFoldAsync(Scope, fold!, "must not install", "test", default)).ShouldBeFalse();
        (await store.ReadAsync(Scope, default)).Summary.ShouldBeNull();
    }

    [Fact]
    public async Task Source_edit_invalidates_already_covered_summary_in_message_transaction()
    {
        await SeedAsync();
        await HistoryAsync(30);
        var original = await Db.Messages.OrderBy(x => x.Id).FirstAsync();
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        (await store.CommitFoldAsync(Scope, fold!, "old summary", "test", default)).ShouldBeTrue();
        var edit = new IncomingMessage(Scope.ChatId, "supergroup", null, 7, original.TelegramMessageId,
            111, "test_user", "edited invented source", MessageKind.Text, true, Now, Now, null, "{}", null, null);
        var outcome = await new MessageStore(db, new Clock(), NullLogger<MessageStore>.Instance).StoreAsync(999, 101, edit, default);
        outcome.Outcome.ShouldBe(Assistant.Application.Messages.StoreOutcome.Updated);
        (await store.ReadAsync(Scope, default)).Summary.ShouldBeNull();
        (await Db.Messages.AsNoTracking().SingleAsync(x => x.Id == original.Id)).Text.ShouldBe("edited invented source");
        (await store.SearchAsync(Scope, "edited", default)).Select(x => x.MessageId).ShouldBe([original.Id]);
        (await store.SearchAsync(Scope, "historical", default)).Select(x => x.MessageId).ShouldNotContain(original.Id);
    }

    [Fact]
    public async Task Revoked_authority_and_unset_family_cannot_read_or_install_memory()
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        await Db.FamilyMembers.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, FamilyMemberStatus.Denied));
        await Should.ThrowAsync<InvalidOperationException>(() => store.CommitFoldAsync(Scope, fold!, "unauthorized", "test", default));
        (await db.Set<GeneralMemoryState>().AsNoTracking().SingleAsync()).SummaryText.ShouldBe("");
        _family.Set(null);
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadAsync(Scope, default));
    }

    [Theory]
    [InlineData("family")]
    [InlineData("bot")]
    [InlineData("place")]
    [InlineData("topic")]
    [InlineData("member")]
    [InlineData("private_identity")]
    public async Task Forged_scope_cannot_read_memory(string change)
    {
        await SeedAsync();
        var scope = change switch
        {
            "family" => Scope with { FamilyId = 12 },
            "bot" => Scope with { BotId = 998 },
            "place" => Scope with { ChatId = -1002222222222 },
            "topic" => Scope with { TopicId = null },
            "member" => Scope with { ActorUserId = 222 },
            _ => Scope with { ChatType = "private", ChatId = 222, TopicId = null }
        };
        await using var db = ScopedDb();
        await Should.ThrowAsync<InvalidOperationException>(() => Store(db).ReadAsync(scope, default));
    }

    [Theory]
    [InlineData("bot")]
    [InlineData("place")]
    public async Task Revoked_bot_or_place_cannot_install_fold(string target)
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        if (target == "bot") await Db.Bots.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, BotStatus.Disabled));
        else await Db.Places.Where(x => x.TopicId == 7).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PlaceStatus.Denied));
        await Should.ThrowAsync<InvalidOperationException>(() => store.CommitFoldAsync(Scope, fold!, "must not install", "test", default));
        (await db.Set<GeneralMemoryState>().AsNoTracking().SingleAsync()).SummaryText.ShouldBe("");
    }

    [Fact]
    public async Task Summary_version_conflict_or_repeat_cannot_overwrite_committed_window()
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        (await store.CommitFoldAsync(Scope, fold!, "first committed summary", "test", default)).ShouldBeTrue();
        (await store.CommitFoldAsync(Scope, fold!, "stale replacement", "test", default)).ShouldBeFalse();
        (await store.ReadAsync(Scope, default)).Summary!.Text.ShouldBe("first committed summary");
    }

    [Fact]
    public async Task Private_scope_is_exact_and_empty_or_punctuation_search_returns_no_rows()
    {
        await SeedAsync();
        var command = await MessageAsync("/remember group fact");
        await using var db = ScopedDb();
        var store = Store(db);
        await store.RememberAsync(Scope, command.Id, "group fact", default);
        (await store.ReadAsync(Scope with { ChatType = "private", ChatId = 111, TopicId = null }, default)).Facts.ShouldBeEmpty();
        (await store.SearchAsync(Scope, "", default)).ShouldBeEmpty();
        (await store.SearchAsync(Scope, "!!!", default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Remember_accepts_exact_500_character_boundary()
    {
        await SeedAsync();
        var text = new string('x', 500);
        var command = await MessageAsync("/remember " + text);
        await using var db = ScopedDb();
        var result = await Store(db).RememberAsync(Scope, command.Id, text, default);
        result.Failure.ShouldBeNull();
        (await Store(db).ReadAsync(Scope, default)).Facts.ShouldHaveSingleItem().Text.ShouldBe(text);
    }

    [Fact]
    public async Task Another_approved_bot_and_family_cannot_leak_through_search_OR()
    {
        await SeedAsync();
        var own = await MessageAsync("invented sharedmarker");
        Db.Families.Add(new Family { Id = 12, Name = "Other synthetic family", CreatedAt = Now });
        Db.Bots.Add(new Bot { Id = 23, FamilyId = 12, TelegramBotId = 998, Username = "other_test_bot",
            Role = "general", Status = BotStatus.Active, CreatedAt = Now });
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = 12, TelegramUserId = 111,
            Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        Db.Places.Add(new Place { BotId = 23, ChatId = Scope.ChatId, TopicId = 7,
            Status = PlaceStatus.Approved, CreatedAt = Now });
        var other = new StoredMessage { FamilyId = 12, BotId = 998, ChatId = Scope.ChatId, TopicId = 7,
            TelegramMessageId = 2222, UserId = 111, Direction = MessageDirection.In, Kind = MessageKind.Text,
            ChatType = "supergroup", Text = "other sharedmarker", Raw = "{}", SentAt = Now, CreatedAt = Now };
        Db.Messages.Add(other);
        await Db.SaveChangesAsync();
        await using var db = ScopedDb();
        (await Store(db).SearchAsync(Scope, "sharedmarker", default)).Select(x => x.MessageId).ShouldBe([own.Id]);
        _family.Set(12);
        (await Store(db).SearchAsync(Scope with { FamilyId = 12, BotDbId = 23, BotId = 998 },
            "sharedmarker", default)).Select(x => x.MessageId).ShouldBe([other.Id]);
    }

    [Fact]
    public async Task Capacity_is_atomic_under_independent_contexts()
    {
        await SeedAsync();
        await using (var setup = ScopedDb())
        {
            for (var i = 0; i < 49; i++) setup.Set<GeneralMemoryFact>().Add(new GeneralMemoryFact
            { FamilyId = 11, BotId = 999, ChatId = Scope.ChatId, TopicId = 7, SourceMessageId = 10000 + i,
                ActorUserId = 111, Text = $"invented fact {i}", CreatedAt = Now });
            await setup.SaveChangesAsync();
        }
        var one = await MessageAsync("/remember last candidate one");
        var two = await MessageAsync("/remember last candidate two");
        await using var dbOne = ScopedDb();
        await using var dbTwo = ScopedDb();
        var outcomes = await Task.WhenAll(Store(dbOne).RememberAsync(Scope, one.Id, "last candidate one", default),
            Store(dbTwo).RememberAsync(Scope, two.Id, "last candidate two", default));
        outcomes.Count(x => x.FactId is not null).ShouldBe(1);
        outcomes.Count(x => x.Failure == "full").ShouldBe(1);
        await using var check = ScopedDb();
        (await Store(check).ReadAsync(Scope, default)).Facts.Count.ShouldBe(50);
    }

    [Fact]
    public async Task Failed_summary_commit_does_not_poison_later_outgoing_message_save()
    {
        await SeedAsync();
        await HistoryAsync(30);
        await using var db = ScopedDb();
        var store = Store(db);
        var fold = await store.PrepareFoldAsync(Scope, long.MaxValue, 10, default);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE general_memory_states ADD CONSTRAINT ck_test_reject_summary CHECK (summary_text <> 'rejected');");
        await Should.ThrowAsync<DbUpdateException>(() => store.CommitFoldAsync(Scope, fold!, "rejected", "test", default));
        await new MessageStore(db, new Clock(), NullLogger<MessageStore>.Instance)
            .StoreOutgoingAsync(999, Scope.ChatId, 7, "supergroup", 9999, "ordinary reply after failed summary", default);
        (await Db.Messages.AsNoTracking().SingleAsync(x => x.TelegramMessageId == 9999)).Text.ShouldBe("ordinary reply after failed summary");
        (await store.ReadAsync(Scope, default)).Summary.ShouldBeNull();
    }

    [Fact]
    public async Task Failed_fact_write_does_not_poison_following_fact_save()
    {
        await SeedAsync();
        var rejected = await MessageAsync("/remember rejected");
        var accepted = await MessageAsync("/remember accepted");
        await using var db = ScopedDb();
        var store = Store(db);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE general_memory_facts ADD CONSTRAINT ck_test_reject_fact CHECK (text <> 'rejected');");
        await Should.ThrowAsync<DbUpdateException>(() => store.RememberAsync(Scope, rejected.Id, "rejected", default));
        var saved = await store.RememberAsync(Scope, accepted.Id, "accepted", default);
        saved.Failure.ShouldBeNull();
        (await store.ReadAsync(Scope, default)).Facts.ShouldHaveSingleItem().Text.ShouldBe("accepted");
    }
}
