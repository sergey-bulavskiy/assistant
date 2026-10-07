using System.Data.Common;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Vet;

public sealed class VetFailureTests : VetTestBase
{
    [Fact]
    public async Task Recovery_limit_does_not_let_unlinked_or_revoked_sources_starve_authorized_linked_work()
    {
        await SeedAsync(); await using var s = Open();
        for (var index = 0; index < 5; index++)
            await s.Diary.AdmitAsync(Scope, Text("synthetic unlinked source", 1000 + index)
                with { SentAt = Now.AddDays(-1) }, index + 1, CancellationToken.None);
        for (var index = 0; index < 5; index++)
        {
            var message = Text("synthetic subsequently revoked source", 1100 + index, actor: 222)
                with { SentAt = Now.AddDays(-1) };
            var source = await s.Diary.AdmitAsync(Scope, message, index + 6, CancellationToken.None);
            var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, index + 6, message, CancellationToken.None);
            await s.Diary.LinkMessageAsync(Scope, source.Source.Id, stored.MessageDbId!.Value, CancellationToken.None);
        }
        await Db.FamilyMembers.Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, Assistant.Domain.Families.FamilyMemberStatus.Denied));
        var eligible = await EvidenceAsync(s, id: 2000, update: 100);
        await s.Diary.SaveWorkAsync(Scope, eligible.Source.Revision.Id, JsonSerializer.Serialize(
            new VetTextPlan([new(null, null, eligible.State)], null)), CancellationToken.None);
        await s.Handler.ResumeAsync(Bot, s.Telegram, CancellationToken.None);
        (await s.Context.VetEvents.SingleAsync()).SourceId.ShouldBe(eligible.Source.Source.Id);
        s.Chat.RequestedMessages.ShouldBeEmpty();
        (await s.Context.VetTextSourceRevisions.CountAsync(r => r.State == "admitted")).ShouldBe(10);
    }

    [Fact]
    public async Task Failed_diary_write_reports_uncertain_status_then_recovery_uses_exact_work_without_new_model_call()
    {
        await SeedAsync();
        var failure = new SqlFailure("INSERT INTO vet_diary_action_changes") { Armed = true };
        await using (var interrupted = Open(interceptor: failure))
        {
            interrupted.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"}]}""");
            await interrupted.Handler.HandleAsync(Bot, interrupted.Telegram, new(1, Text("synthetic interrupted write")), CancellationToken.None);
            interrupted.Telegram.SentMessages.Single().Text.ShouldBe(VetAssistant.NotSavedText);
            (await interrupted.Context.VetEvents.CountAsync()).ShouldBe(0);
            (await interrupted.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
            (await interrupted.Context.VetExtractionResults.CountAsync()).ShouldBe(1);
            (await interrupted.Context.VetTextSourceRevisions.SingleAsync()).State.ShouldBe("ready");
        }
        await using var recovered = Open();
        await recovered.Handler.ResumeAsync(Bot, recovered.Telegram, CancellationToken.None);
        recovered.Chat.RequestedMessages.ShouldBeEmpty();
        (await recovered.Context.VetEvents.SingleAsync()).Value.ShouldBe(6.4m);
        (await recovered.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        (await recovered.Context.LlmCalls.CountAsync()).ShouldBe(1);
        recovered.Telegram.SentMessages.Single().Text.ShouldContain("Сохранено:");
    }

    [Fact]
    public async Task Poller_retries_transient_admission_beyond_poison_cap_without_advancing_offset_then_recovers()
    {
        await SeedAsync();
        var failure = new SqlFailure("INSERT INTO vet_text_sources", remaining: 4) { Armed = true };
        var telegram = new Assistant.IntegrationTests.Host.FakeTelegramClient();
        telegram.EnqueueUpdate(new(1, Text("synthetic intake retry")));
        var services = new ServiceCollection();
        services.AddScoped(_ => Open(interceptor: failure, runtimeOptions: new(false)));
        services.AddScoped<IMessageStore>(p => p.GetRequiredService<VetTestSession>().Messages);
        services.AddScoped<UpdateHandler>(p => p.GetRequiredService<VetTestSession>().Handler);
        using var provider = services.BuildServiceProvider();
        var worker = new BotPollingWorker(Bot, telegram, [UpdateKind.Message],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, PoisonUpdateFailureCap: 2),
            new PollingHealth(), Clock, NullLogger.Instance, "test-token");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = worker.RunAsync(cancellation.Token);
        try
        {
            while (!telegram.RequestedOffsets.Contains(2) && !cancellation.IsCancellationRequested)
                await Task.Delay(10, cancellation.Token);
        }
        finally { await cancellation.CancelAsync(); await running; }
        telegram.RequestedOffsets.Take(5).ShouldBe(new long[] { 1, 1, 1, 1, 1 });
        telegram.RequestedOffsets.ShouldContain(2);
        failure.Failures.ShouldBe(4);
        await using var verified = Open();
        (await verified.Messages.GetLastUpdateIdAsync(Bot.TelegramBotId, CancellationToken.None)).ShouldBe(1);
        (await verified.Context.VetTextSources.CountAsync()).ShouldBe(1);
        (await verified.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        (await verified.Context.LlmCalls.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_event_insert_rolls_back_pending_action_and_events_and_context_can_retry(bool cancel)
    {
        await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var failure = new SqlFailure("INSERT INTO vet_diary_action_changes", cancel ? cancellation : null);
        await using var s = Open(interceptor: failure);
        var e = await EvidenceAsync(s);
        var changes = new[] { new VetEventChange(null, null, e.State), new VetEventChange(null, null,
            e.State with { EventType = "insulin", Value = 0.125m, Unit = "U" }) };
        var pending = await s.Diary.PutPendingAsync(Scope, e.Source.Source.Id, e.Source.Revision.Id,
            e.State.ExtractionResultId, 111, JsonSerializer.Serialize(new VetProposal([], changes, [], "confirm")), CancellationToken.None);
        var mutation = new VetDiaryMutation(Scope, pending.OperationKey, 222, "confirm", e.Profile.Id,
            changes, pending.SourceId, pending.InputRevisionId, pending.Id, pending.ReviewRevision);
        failure.Armed = true;
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => s.Diary.ApplyAsync(mutation, cancellation.Token));
        else
            await Should.ThrowAsync<DbUpdateException>(() => s.Diary.ApplyAsync(mutation, CancellationToken.None));
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        (await s.Context.VetDiaryActionChanges.CountAsync()).ShouldBe(0);
        (await s.Diary.GetPendingAsync(Scope, pending.Id, CancellationToken.None))!.State.ShouldBe("pending");
        var retry = await s.Diary.ApplyAsync(mutation, CancellationToken.None);
        retry.Status.ShouldBe(VetMutationStatus.Applied);
        (await s.Context.VetEvents.OrderBy(e => e.EventType).Select(e => e.Value).ToListAsync()).ShouldBe(new[] { 6.4m, 0.125m });
        (await s.Context.VetDiaryActionChanges.CountAsync()).ShouldBe(2);
        (await s.Diary.GetPendingAsync(Scope, pending.Id, CancellationToken.None))!.ResolvedByUserId.ShouldBe(222);
    }

    [Fact]
    public async Task Transient_admission_failure_preserves_transport_offset_and_redelivery_can_record()
    {
        await SeedAsync();
        var failure = new SqlFailure("INSERT INTO vet_text_sources") { Armed = true };
        var message = Text("synthetic transient intake");
        await using (var interrupted = Open(interceptor: failure))
        {
            var ex = await Should.ThrowAsync<VetIntakePersistenceException>(() => interrupted.Handler.HandleAsync(
                Bot, interrupted.Telegram, new(1, message), CancellationToken.None));
            ex.Message.ShouldBe("Vet intake persistence failed.");
            (await interrupted.Messages.GetLastUpdateIdAsync(Bot.TelegramBotId, CancellationToken.None)).ShouldBe(0);
            interrupted.Chat.RequestedMessages.ShouldBeEmpty();
        }
        await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"record","dose":"0.125","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, message), CancellationToken.None);
        (await s.Messages.GetLastUpdateIdAsync(Bot.TelegramBotId, CancellationToken.None)).ShouldBe(1);
        (await s.Context.VetEvents.SingleAsync()).Value.ShouldBe(0.125m);
        (await s.Context.VetTextSources.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Explicit_retries_are_bounded_and_restart_does_not_replay_failed_provider_call()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("invalid synthetic provider json");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic report")), CancellationToken.None);
        var source = await s.Context.VetTextSources.AsNoTracking().SingleAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await s.Handler.ResumeAsync(Bot, s.Telegram, CancellationToken.None);
            s.Chat.RequestedMessages.Count.ShouldBe(attempt + 1);
            await s.Handler.HandleAsync(Bot, s.Telegram, new(attempt + 2,
                Text("/retry", 1001 + attempt) with { ReplyToMessageId = 1000 }), CancellationToken.None);
            s.Chat.EnqueueResponse("invalid synthetic provider json");
            await s.Handler.ResumeAsync(Bot, s.Telegram, CancellationToken.None);
        }
        await s.Handler.HandleAsync(Bot, s.Telegram, new(5,
            Text("/retry", 1004) with { ReplyToMessageId = 1000 }), CancellationToken.None);
        await s.Handler.ResumeAsync(Bot, s.Telegram, CancellationToken.None);
        s.Chat.RequestedMessages.Count.ShouldBe(4);
        var revision = (await s.Diary.GetSourceAsync(Scope, source.Id, CancellationToken.None))!.Revision;
        revision.ExplicitRetryCount.ShouldBe(3); revision.State.ShouldBe("failed");
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Context.LlmCalls.CountAsync()).ShouldBe(4);
        s.Telegram.SentMessages.Last().Text.ShouldContain("предел трёх явных попыток");
    }

    [Fact]
    public async Task Subscription_disabled_refuses_extraction_without_fallback_but_profile_and_history_commands_work()
    {
        await SeedAsync(); await using var s = Open(runtimeOptions: new(false));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic report")), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("/profile", 1001)), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("/today", 1002)), CancellationToken.None);
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync()).ShouldBe(0);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        s.Telegram.SentMessages.Select(m => m.Text).ShouldContain(t => t.Contains("Профиль:"));
        s.Telegram.SentMessages.Last().Text.ShouldContain("всего 0");
    }

    private sealed class SqlFailure(string match, CancellationTokenSource? cancellation = null, int remaining = 1) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains(match, StringComparison.Ordinal))
            {
                Failures++;
                Armed = --remaining > 0;
                if (cancellation is not null) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
                throw new IOException("synthetic transport interruption");
            }
            return ValueTask.FromResult(result);
        }
    }
}
