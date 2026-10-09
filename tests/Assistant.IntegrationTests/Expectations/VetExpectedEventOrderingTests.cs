using Assistant.Application.Expectations;
using Assistant.Application.Reminders;
using Assistant.Application.Vet;
using Assistant.Domain.Expectations;
using Assistant.Infrastructure.Expectations;
using Assistant.IntegrationTests.Vet;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

public sealed class VetExpectedEventOrderingTests : VetTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_vet_apply_and_due_fence_have_proven_commit_order(bool eventWins)
    {
        await SeedAsync(); Guid id; VetDiaryMutation mutation;
        var scope = new ReminderScope(FamilyId, Bot.BotDbId, Bot.TelegramBotId, "vet", -100, 7, "supergroup", 111);
        await using (var setup = Open())
        {
            var profile = await setup.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, default);
            (await setup.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, profile.Revision,
                [new("Name", "Synthetic animal")], default)).Applied.ShouldBeTrue();
            var store = new ExpectationStore(setup.Context, setup.Current, Clock);
            var intake = await store.ExecuteAsync(scope, 901, new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default);
            intake.Result.ShouldBe("preview");
            var preview = (await store.BeginPreviewAsync(scope, intake.DraftId!.Value, default)).ShouldNotBeNull();
            await store.BindPreviewAsync(scope, preview.DraftId, 902, default);
            (await store.ResolveAsync(scope, preview.DraftId, 902, true, default)).ShouldBe("saved"); id = preview.Id;
            var evidence = await EvidenceAsync(setup, id: 1000);
            var state = evidence.State with { OccurredAt = DateTimeOffset.Parse("2031-05-13T08:30:00Z"), LocalTime = "2031-05-13 08:30" };
            mutation = Save(Scope, evidence.Source, evidence.Profile, state);
        }
        Clock.UtcNow = DateTimeOffset.Parse("2031-05-13T09:30:00Z");
        var key = $"{FamilyId}:{Bot.BotDbId}:vet";
        var writerGate = new OrderingRaceGate(key, eventWins); var evaluatorGate = new OrderingRaceGate(key, !eventWins);
        await using var writer = Open(interceptor: writerGate); await using var evaluator = Open(interceptor: evaluatorGate);
        await writer.Context.Database.OpenConnectionAsync(); await evaluator.Context.Database.OpenConnectionAsync();
        var writerPid = ((NpgsqlConnection)writer.Context.Database.GetDbConnection()).ProcessID;
        var evaluatorPid = ((NpgsqlConnection)evaluator.Context.Database.GetDbConnection()).ProcessID;
        var dispatches = new NonurgentDispatchStore(evaluator.Context, evaluator.Current, Clock);
        Task<VetMutationResult>? write = null; Task<NonurgentDispatch?>? claim = null;
        Task<NonurgentDispatch?> StartClaim() => dispatches.ClaimAsync(Bot, new("expectation", id, Clock.UtcNow), default);
        try
        {
            if (eventWins)
            {
                write = writer.Diary.ApplyAsync(mutation, default); await writerGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                claim = StartClaim(); await evaluatorGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, writerPid, evaluatorPid, claim);
            }
            else
            {
                claim = StartClaim(); await evaluatorGate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                write = writer.Diary.ApplyAsync(mutation, default); await writerGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OrderingRaceGate.ProveBlockedAsync(ConnectionString, evaluatorPid, writerPid, write);
            }
        }
        finally { writerGate.Release(); evaluatorGate.Release(); await OrderingRaceGate.JoinAsync(write, claim); }
        var result = await write!; result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.Count.ShouldBe(1);
        await using var read = Open(); var occurrence = await read.Context.Set<ExpectationOccurrence>().SingleAsync();
        if (eventWins)
        {
            (await claim!).ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(result.EventIds[0]);
            (await read.Context.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        else
        {
            var dispatch = (await claim!).ShouldNotBeNull(); dispatch.Kind.ShouldBe("expectation"); dispatch.Id.ShouldBe(id);
            occurrence.Outcome.ShouldBe("dispatch-unknown"); (await read.Context.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
            var restarted = new NonurgentDispatchStore(read.Context, read.Current, Clock);
            (await restarted.ClaimAsync(Bot, new("expectation", id, Clock.UtcNow), default)).ShouldBeNull();
        }
    }
}
