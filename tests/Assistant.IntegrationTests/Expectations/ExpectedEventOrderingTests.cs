using Assistant.Application.Expectations;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;
using Assistant.Domain.Expectations;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Health.Documents;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

public sealed class ExpectedEventOrderingTests : ExpectationTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_health_add_and_due_fence_have_proven_commit_order(bool eventWins)
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
        var writerGate = new OrderingRaceGate("11:22:health", eventWins);
        var evaluatorGate = new OrderingRaceGate("11:22:health", !eventWins);
        await using var writer = Open(11, writerGate); await using var evaluator = Open(11, evaluatorGate);
        await writer.Db.Database.OpenConnectionAsync(); await evaluator.Db.Database.OpenConnectionAsync();
        var writerPid = ((NpgsqlConnection)writer.Db.Database.GetDbConnection()).ProcessID;
        var evaluatorPid = ((NpgsqlConnection)evaluator.Db.Database.GetDbConnection()).ProcessID;
        var events = new EventStore(writer.Db, writer.Family, Clock);
        Task<IReadOnlyList<HealthEventInfo>>? write = null; Task<NonurgentDispatch?>? claim = null;
        Task<IReadOnlyList<HealthEventInfo>> StartWrite() => events.AddAsync(11, 333, new(null, 999, -100, 7, 222),
            [new("glucose", DateTimeOffset.Parse("2032-02-10T05:30:00Z"), "stated", "{\"mmolL\":6.2}")], default);
        Task<NonurgentDispatch?> StartClaim() => evaluator.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default);
        try
        {
            if (eventWins)
            {
                write = StartWrite(); await writerGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                claim = StartClaim(); await evaluatorGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, writerPid, evaluatorPid, claim);
            }
            else
            {
                claim = StartClaim(); await evaluatorGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                write = StartWrite(); await writerGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, evaluatorPid, writerPid, write);
            }
        }
        finally { writerGate.Release(); evaluatorGate.Release(); await OrderingRaceGate.JoinAsync(write, claim); }
        var saved = (await write!).ShouldHaveSingleItem(); await using var read = Open();
        var occurrence = await read.Db.Set<ExpectationOccurrence>().AsNoTracking().SingleAsync();
        if (eventWins)
        {
            (await claim!).ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(saved.Id);
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        else
        {
            var dispatch = (await claim!).ShouldNotBeNull(); occurrence.Outcome.ShouldBe("dispatch-unknown");
            dispatch.Text.ShouldBe("На момент проверки нет подтверждённой записи: synthetic_subject, glucose, за 2032-02-10 с 00:00 до 09:30 UTC+03:00. Проверяется только наличие записи; это не означает, что действие не выполнено.");
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
            (await read.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Real_pending_acceptance_and_due_fence_share_transaction_and_document_source_order(bool eventWins, bool documentSource)
    {
        await SeedAsync(); var active = await ActiveAsync(); long? messageId = null; long pendingId;
        var reading = new NewHealthEvent("glucose", DateTimeOffset.Parse("2032-02-10T05:30:00Z"), "stated", "{\"mmolL\":6.2}");
        await using (var setup = Open())
        {
            if (documentSource)
            {
                var scope = new HealthDocumentScope(11, 333, 22, 999);
                var message = new IncomingMessage(-100, "supergroup", null, 7, 500, 222, "synthetic_member", "synthetic caption",
                    MessageKind.Document, false, Initial, null, null, "{}", null, null,
                    new DocumentAttachment("synthetic-file", "synthetic-unique", "synthetic.txt", "text/plain", 20));
                var documents = new HealthDocumentStore(setup.Db, setup.Family, Clock);
                var admission = (await documents.AdmitAsync(scope, message, 600, default)).ShouldNotBeNull();
                var messages = new MessageStore(setup.Db, Clock, NullLogger<MessageStore>.Instance);
                var stored = await messages.StoreAsync(999, 600, message, default); messageId = stored.MessageDbId;
                var bound = (await documents.BindAsync(scope, admission.Id, messageId, default)).ShouldNotBeNull();
                bound.SourceMessageId.ShouldBe(messageId); messageId.ShouldNotBeNull();
            }
            var pending = new PendingRecordStore(setup.Db, setup.Family, Clock);
            pendingId = await pending.AddAsync(11, 333, new(messageId, 999, -100, 7, 500, 222, [reading], []), default);
        }
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
        var writerGate = new OrderingRaceGate("11:22:health", eventWins, documentSource);
        var evaluatorGate = new OrderingRaceGate("11:22:health", !eventWins);
        await using var writer = Open(11, writerGate); await using var evaluator = Open(11, evaluatorGate);
        await writer.Db.Database.OpenConnectionAsync(); await evaluator.Db.Database.OpenConnectionAsync();
        var writerPid = ((NpgsqlConnection)writer.Db.Database.GetDbConnection()).ProcessID;
        var evaluatorPid = ((NpgsqlConnection)evaluator.Db.Database.GetDbConnection()).ProcessID;
        var pendingStore = new PendingRecordStore(writer.Db, writer.Family, Clock);
        var eventStore = new EventStore(writer.Db, writer.Family, Clock);
        IReadOnlyList<HealthEventInfo>? saved = null;
        Task<bool>? accept = null; Task<NonurgentDispatch?>? claim = null;
        Task<bool> StartAccept() => pendingStore.TryResolveAsync(11, pendingId, PendingRecordStatuses.Accepted, 111,
            async ct => saved = await eventStore.AddAsync(11, 333, new(messageId, 999, -100, 7, 222), [reading], ct), default);
        Task<NonurgentDispatch?> StartClaim() => evaluator.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default);
        try
        {
            if (eventWins)
            {
                accept = StartAccept(); await writerGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                claim = StartClaim(); await evaluatorGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, writerPid, evaluatorPid, claim);
            }
            else
            {
                claim = StartClaim(); await evaluatorGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                accept = StartAccept(); await writerGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, evaluatorPid, writerPid, accept);
            }
        }
        finally { writerGate.Release(); evaluatorGate.Release(); await OrderingRaceGate.JoinAsync(accept, claim); }
        (await accept!).ShouldBeTrue(); var fact = saved.ShouldNotBeNull().ShouldHaveSingleItem();
        await using var read = Open(); var pendingRow = await read.Db.PendingRecords.SingleAsync(x => x.Id == pendingId);
        pendingRow.Status.ShouldBe(PendingRecordStatuses.Accepted); pendingRow.ResolvedByUserId.ShouldBe(111);
        var occurrence = await read.Db.Set<ExpectationOccurrence>().SingleAsync();
        if (eventWins)
        {
            (await claim!).ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(fact.Id);
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        else
        {
            (await claim!).ShouldNotBeNull(); occurrence.Outcome.ShouldBe("dispatch-unknown"); occurrence.MatchedEventId.ShouldBeNull();
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_health_delete_winning_before_evaluation_sends_but_evaluation_first_stays_satisfied(bool mutationWins)
    {
        await SeedAsync(); var active = await ActiveAsync(); long eventId;
        await using (var setup = Open())
        {
            var store = new EventStore(setup.Db, setup.Family, Clock);
            eventId = (await store.AddAsync(11, 333, new(null, 999, -100, 7, 222),
                [new("glucose", DateTimeOffset.Parse("2032-02-10T05:30:00Z"), "stated", "{\"mmolL\":6.2}")], default)).ShouldHaveSingleItem().Id;
        }
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
        var writerGate = new OrderingRaceGate("11:22:health", mutationWins);
        var evaluatorGate = new OrderingRaceGate("11:22:health", !mutationWins);
        await using var writer = Open(11, writerGate); await using var evaluator = Open(11, evaluatorGate);
        await writer.Db.Database.OpenConnectionAsync(); await evaluator.Db.Database.OpenConnectionAsync();
        var writerPid = ((NpgsqlConnection)writer.Db.Database.GetDbConnection()).ProcessID;
        var evaluatorPid = ((NpgsqlConnection)evaluator.Db.Database.GetDbConnection()).ProcessID;
        var events = new EventStore(writer.Db, writer.Family, Clock);
        Task<DeletedEvents>? deletion = null; Task<NonurgentDispatch?>? claim = null;
        Task<DeletedEvents> StartDelete() => events.DeleteByIdAsync(11, 333, eventId, "del", default);
        Task<NonurgentDispatch?> StartClaim() => evaluator.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default);
        try
        {
            if (mutationWins)
            {
                deletion = StartDelete(); await writerGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                claim = StartClaim(); await evaluatorGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, writerPid, evaluatorPid, claim);
            }
            else
            {
                claim = StartClaim(); await evaluatorGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                deletion = StartDelete(); await writerGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, evaluatorPid, writerPid, deletion);
            }
        }
        finally { writerGate.Release(); evaluatorGate.Release(); await OrderingRaceGate.JoinAsync(deletion, claim); }
        (await deletion!).Events.ShouldHaveSingleItem().Id.ShouldBe(eventId);
        await using var read = Open();
        (await read.Db.Events.AsNoTracking().SingleAsync(x => x.Id == eventId)).DeletedAt.ShouldBe(Clock.UtcNow);
        var occurrence = await read.Db.Set<ExpectationOccurrence>().SingleAsync();
        if (mutationWins)
        {
            (await claim!).ShouldNotBeNull(); occurrence.Outcome.ShouldBe("dispatch-unknown"); occurrence.MatchedEventId.ShouldBeNull();
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        }
        else
        {
            (await claim!).ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(eventId);
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        (await read.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldBeNull();
        (await read.Db.Set<ExpectationOccurrence>().AsNoTracking().SingleAsync()).Outcome
            .ShouldBe(mutationWins ? "dispatch-unknown" : "satisfied");
    }
}
