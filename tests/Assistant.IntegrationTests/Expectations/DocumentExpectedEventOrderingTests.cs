using Assistant.Application.Expectations;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Domain.Expectations;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Health.Documents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

public sealed class DocumentExpectedEventOrderingTests : ExpectationTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Document_caption_deletion_and_evaluation_have_proven_commit_order(bool deletionWins)
    {
        await SeedAsync(); var active = await ActiveAsync();
        var occurred = DateTimeOffset.Parse("2032-02-10T05:30:00Z");
        var admissionId = Guid.NewGuid(); long eventId;
        await using (var setup = Open())
        {
            var message = new StoredMessage { FamilyId = 11, BotId = 999, ChatId = -100, TopicId = 7,
                TelegramMessageId = 900, UserId = 222, ChatType = "supergroup", Kind = MessageKind.Document,
                Direction = MessageDirection.In, SentAt = occurred, CreatedAt = occurred, Raw = "{}" };
            setup.Db.Add(message); await setup.Db.SaveChangesAsync();
            setup.Db.Add(new HealthDocumentAdmission { Id = admissionId, FamilyId = 11, ProfileId = 333,
                BotDbId = 22, TelegramBotId = 999, ChatId = -100, TopicId = 7, ChatType = "supergroup",
                TelegramMessageId = 900, SenderUserId = 222, FirstUpdateId = 900, SentAt = occurred,
                SourceMessageId = message.Id, FileId = "synthetic-file", FileName = "synthetic.txt",
                MimeType = "text/plain", CreatedAt = occurred, UpdatedAt = occurred });
            await setup.Db.SaveChangesAsync();
            var saved = await new EventStore(setup.Db, setup.Family, Clock).AddAsync(11, 333,
                new(message.Id, 999, -100, 7, 222),
                [new("glucose", occurred, "stated", "{\"mmolL\":6.2}")], default);
            eventId = saved.ShouldHaveSingleItem().Id;
        }
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
        var deleteGate = new OrderingRaceGate("11:22:health", deletionWins, requireDocumentSource: true);
        var evalGate = new OrderingRaceGate("11:22:health", !deletionWins);
        await using var writer = Open(11, deleteGate); await using var evaluator = Open(11, evalGate);
        await writer.Db.Database.OpenConnectionAsync(); await evaluator.Db.Database.OpenConnectionAsync();
        var writerPid = ((NpgsqlConnection)writer.Db.Database.GetDbConnection()).ProcessID;
        var evalPid = ((NpgsqlConnection)evaluator.Db.Database.GetDbConnection()).ProcessID;
        Task<HealthDocumentSourceDeletion>? deletion = null;
        Task<NonurgentDispatch?>? claim = null;
        Task<HealthDocumentSourceDeletion> Delete() => new HealthDocumentStore(writer.Db, writer.Family, Clock)
            .DeleteSourceAsync(new(11, 333, 22, 999), -100, 7, 900, 111, default);
        Task<NonurgentDispatch?> Claim() => evaluator.Dispatch.ClaimAsync(HealthBot,
            new("expectation", active.Id, Clock.UtcNow), default);
        try
        {
            if (deletionWins)
            {
                deletion = Delete(); await deleteGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                claim = Claim(); await evalGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, writerPid, evalPid, claim);
            }
            else
            {
                claim = Claim(); await evalGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                deletion = Delete(); await deleteGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, evalPid, writerPid, deletion);
            }
        }
        finally { deleteGate.Release(); evalGate.Release(); await OrderingRaceGate.JoinAsync(deletion, claim); }
        var removed = await deletion!;
        removed.DocumentDeleted.ShouldBeTrue(); removed.DeletedEvents.Events.ShouldHaveSingleItem().Id.ShouldBe(eventId);
        await using var read = Open();
        (await read.Db.HealthDocumentAdmissions.SingleAsync(x => x.Id == admissionId)).DeletedAt.ShouldBe(Clock.UtcNow);
        (await read.Db.Events.SingleAsync(x => x.Id == eventId)).DeletedAt.ShouldBe(Clock.UtcNow);
        var occurrence = await read.Db.Set<ExpectationOccurrence>().SingleAsync();
        if (deletionWins)
        {
            (await claim!).ShouldNotBeNull().Id.ShouldBe(active.Id);
            occurrence.Outcome.ShouldBe("dispatch-unknown");
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        }
        else
        {
            (await claim!).ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied");
            occurrence.MatchedEventId.ShouldBe(eventId);
            (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
            (await read.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldBeNull();
            (await read.Db.Set<ExpectationOccurrence>().AsNoTracking().SingleAsync()).Outcome.ShouldBe("satisfied");
        }
    }
}
