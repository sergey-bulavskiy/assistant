using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging;

namespace Assistant.UnitTests.Reminders;

public sealed class ReminderAssistantTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReceivingBot Bot = new(22, 999, "test_bot", 11, "general");
    private static readonly ReminderScope Scope = new(11, 22, 999, "general", -1001111111111, 7, "supergroup", 111);
    private static IncomingMessage Message(string text = "/remind in 10m synthetic task") => new(
        Scope.ChatId, "supergroup", "Synthetic group", 7, 100, 111, "synthetic_user", text,
        MessageKind.Text, false, Now, null, null, "{}", null, null);
    private readonly Store _store = new();
    private readonly CurrentFamily _family = new();
    private readonly Logs<ReminderAssistant> _logs = new();
    private ReminderAssistant Sut() => new(_store, new FixedClock(Now), _family, _logs);
    private sealed class Logs<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, ex));
    }
    private sealed class Store : IReminderStore
    {
        public ReminderPreferences Preferences = new();
        public ReminderItem Item = new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "synthetic task", "draft", Now.AddMinutes(10), null, 0, Now);
        public List<(ReminderScope Scope, int Source, ReminderRequest Request)> Admitted = [];
        public List<(ReminderScope Scope, Guid Id, int Message)> Bound = [];
        public List<(ReminderScope Scope, Guid Id, int Message)> Saved = [];
        public List<(ReminderScope Scope, Guid Id)> Cancelled = [];
        public List<ReminderDispatch> Completed = [];
        public Queue<ReminderDispatch> Due = new();
        public string Resolution = "saved";
        public bool PreviewBegan;
        public bool FailBind;
        public bool FailComplete;
        public int Cleanups;
        public Exception? AdmissionFailure;
        public Task<ReminderPreferences> GetPreferencesAsync(ReminderScope s, CancellationToken ct) => Task.FromResult(Preferences);
        public Task SetPreferencesAsync(ReminderScope s, int source, ReminderPreferences p, CancellationToken ct) { Preferences=p; return Task.CompletedTask; }
        public Task<ReminderItem?> AdmitAsync(ReminderScope s, int source, ReminderRequest r, CancellationToken ct)
        {
            if (AdmissionFailure != null) throw AdmissionFailure;
            Admitted.Add((s, source, r)); return Task.FromResult<ReminderItem?>(Item);
        }
        public Task<IReadOnlyList<ReminderItem>> ListAsync(ReminderScope s, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReminderItem>>([Item]);
        public Task<ReminderItem?> BeginPreviewAsync(ReminderScope s, Guid id, CancellationToken ct)
        { if (PreviewBegan) return Task.FromResult<ReminderItem?>(null); PreviewBegan=true; return Task.FromResult<ReminderItem?>(Item); }
        public Task BindPreviewAsync(ReminderScope s, Guid id, int message, CancellationToken ct)
        { if(FailBind) throw new IOException("synthetic-private-text test-token"); Bound.Add((s,id,message)); return Task.CompletedTask; }
        public Task<string> SaveAsync(ReminderScope s, Guid id, int message, CancellationToken ct)
        { Saved.Add((s,id,message)); return Task.FromResult(Resolution); }
        public Task<string> CancelAsync(ReminderScope s, Guid id, CancellationToken ct)
        { Cancelled.Add((s,id)); return Task.FromResult(Resolution); }
        public Task<ReminderDispatch?> ClaimDueAsync(ReceivingBot bot,CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult<ReminderDispatch?>(Due.TryDequeue(out var d) ? d : null); }
        public Task CompleteAsync(ReceivingBot bot, ReminderDispatch d,int message,CancellationToken ct)
        { if(FailComplete) throw new IOException("synthetic-private-text test-token"); Completed.Add(d); return Task.CompletedTask; }
        public Task CleanupAsync(ReceivingBot bot,CancellationToken ct) { ct.ThrowIfCancellationRequested(); Cleanups++; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("general")]
    [InlineData("health")]
    [InlineData("vet")]
    public async Task All_role_commands_admit_exact_source_before_handling(string role)
    {
        var intake = await Sut().AdmitAsync(Bot with { Role=role }, Message(), false, default);
        intake!.Kind.ShouldBe("create"); intake.Id.ShouldBe(_store.Item.Id);
        var admitted = _store.Admitted.ShouldHaveSingleItem();
        admitted.Scope.ShouldBe(Scope with { Role=role }); admitted.Source.ShouldBe(100);
        admitted.Request.ShouldBe(new ReminderRequest("synthetic task", Now.AddMinutes(10), null, 0));
        _store.PreviewBegan.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Unmentioned_conversation_requires_exact_place_autoreply(bool replyToAll, bool accepted)
    {
        var result = await Sut().AdmitAsync(Bot, Message("напомни через 10 минут synthetic task"), replyToAll, default);
        (result != null).ShouldBe(accepted);
        _store.Admitted.Count.ShouldBe(accepted ? 1 : 0);
    }

    [Fact]
    public async Task Private_and_real_bot_reply_allow_conversational_requests()
    {
        await Sut().AdmitAsync(Bot, Message("напомни через 10 минут synthetic task") with
            { ChatType="private", ChatId=111, TopicId=null }, false, default);
        await Sut().AdmitAsync(Bot, Message("напомни через 10 минут synthetic task") with
            { ReplyToUserId=999, ReplyToMessageId=50 }, false, default);
        _store.Admitted.Count.ShouldBe(2);
        _store.Admitted[0].Scope.ChatId.ShouldBe(111);
        _store.Admitted[1].Scope.TopicId.ShouldBe(7);
    }

    [Fact]
    public async Task Unrelated_other_bot_topic_root_nontext_and_no_actor_do_not_admit()
    {
        var sut=Sut();
        (await sut.AdmitAsync(Bot, Message("ordinary synthetic text"), true, default)).ShouldBeNull();
        (await sut.AdmitAsync(Bot, Message("/remind@other_bot in 10m task"), true, default)).ShouldBeNull();
        (await sut.AdmitAsync(Bot, Message("напомни через 10 минут task") with {ReplyToUserId=999,ReplyToMessageId=7}, false, default)).ShouldBeNull();
        (await sut.AdmitAsync(Bot, Message() with {Kind=MessageKind.Photo}, true, default)).ShouldBeNull();
        var noActor = (await sut.AdmitAsync(Bot, Message() with {UserId=null}, true, default))!;
        noActor.Kind.ShouldBe("invalid"); noActor.Error.ShouldBe("У вас нет прав.");
        _store.Admitted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edited_request_replies_with_cancel_recreate_without_admission()
    {
        var message=Message() with {IsEdit=true}; var sut=Sut();
        var intake=(await sut.AdmitAsync(Bot,message,false,default))!;
        var transport=new FakeTelegramClient(); await sut.HandleAsync(intake,transport,message,new(StoreOutcome.Updated,1),default);
        transport.Sent.ShouldHaveSingleItem().Text.ShouldBe("Изменения не меняют напоминания. Отмените старое и отправьте новый запрос.");
        _store.Admitted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Preview_is_unscheduled_exact_destination_with_save_cancel_and_bound_message()
    {
        var sut=Sut(); var message=Message(); var intake=(await sut.AdmitAsync(Bot,message,false,default))!;
        var transport=new FakeTelegramClient(); await sut.HandleAsync(intake,transport,message,new(StoreOutcome.Stored,1),default);
        var preview=transport.ButtonMessages.ShouldHaveSingleItem();
        preview.ChatId.ShouldBe(Scope.ChatId); preview.TopicId.ShouldBe(7); preview.ReplyToMessageId.ShouldBe(100);
        preview.Text.ShouldContain("Ничего не запланировано до Сохранить"); preview.Text.ShouldContain("synthetic task");
        preview.Text.ShouldContain("2026-01-02 12:10 UTC+00:00"); preview.Text.ShouldContain("22:00–08:00");
        preview.Buttons.Select(x=>x.Label).ShouldBe(["Сохранить","Отмена"]);
        _store.Bound.ShouldHaveSingleItem().Message.ShouldBe(preview.MessageId); _store.Saved.ShouldBeEmpty();
        await sut.HandleAsync(intake,transport,message,new(StoreOutcome.Duplicate,1),default);
        transport.ButtonMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Preview_binding_failure_keeps_unbound_and_logs_type_without_private_payload()
    {
        _store.FailBind=true; var sut=Sut(); var message=Message();
        var intake=(await sut.AdmitAsync(Bot,message,false,default))!; var transport=new FakeTelegramClient();
        await sut.HandleAsync(intake,transport,message,new(StoreOutcome.Stored,1),default);
        _store.Bound.ShouldBeEmpty(); _store.PreviewBegan.ShouldBeTrue();
        _logs.Lines.ShouldHaveSingleItem().ShouldBe("Reminder preview outcome unknown: IOException");
        await sut.HandleAsync(intake,transport,message,new(StoreOutcome.Duplicate,1),default);
        transport.ButtonMessages.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("saved","Напоминание сохранено.")]
    [InlineData("cancelled","Напоминание отменено.")]
    [InlineData("cancelled_started","Будущие отправки отменены; начатая отправка могла уже уйти.")]
    [InlineData("expired","Время прошло или предварительный просмотр истёк. Создайте новый запрос.")]
    [InlineData("unavailable","Недоступно.")]
    public async Task Save_callback_passes_actual_actor_place_preview_and_reports_resolution(string result,string expected)
    {
        _store.Resolution=result; var transport=new FakeTelegramClient();
        await Sut().HandleCallbackAsync(Bot,transport,new("synthetic-callback",222,$"rem_save:{_store.Item.Id:N}",Scope.ChatId,700,8,"supergroup"),default);
        var saved=_store.Saved.ShouldHaveSingleItem();
        saved.Scope.ShouldBe(Scope with {ActorUserId=222,TopicId=8}); saved.Message.ShouldBe(700);
        transport.AnsweredCallbacks.ShouldHaveSingleItem().Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("rem_save:bad")]
    [InlineData("rem_save:10000000-0000-0000-0000-000000000001")]
    [InlineData("rem_save:00000000000000000000000000000000:extra")]
    public async Task Malformed_callbacks_answer_unavailable_without_mutation(string data)
    {
        var transport=new FakeTelegramClient(); await Sut().HandleCallbackAsync(Bot,transport,new("synthetic-callback",111,data,Scope.ChatId,700,7,"supergroup"),default);
        transport.AnsweredCallbacks.ShouldHaveSingleItem().Text.ShouldBe("Недоступно.");
        _store.Saved.ShouldBeEmpty(); _store.Cancelled.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tick_uses_supplied_transport_family_scope_and_five_dispatch_bound()
    {
        for(var i=0;i<6;i++) _store.Due.Enqueue(new(Guid.NewGuid(),Guid.NewGuid(),Scope.ChatId,7,$"invented task {i}"));
        var transport=new FakeTelegramClient(); await Sut().TickAsync(Bot,transport,default);
        _family.FamilyId.ShouldBe(11); transport.Sent.Select(x=>x.Text).ShouldBe(["invented task 0","invented task 1","invented task 2","invented task 3","invented task 4"]);
        transport.Sent.ShouldAllBe(x=>x.ChatId==Scope.ChatId && x.TopicId==7 && x.ReplyToMessageId==null);
        _store.Completed.Count.ShouldBe(5); _store.Due.Count.ShouldBe(1); _store.Cleanups.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_transport_or_completion_never_retries_and_logs_only_type(bool completion)
    {
        _store.Due.Enqueue(new(Guid.NewGuid(),Guid.NewGuid(),Scope.ChatId,7,"synthetic-private-text"));
        _store.FailComplete=completion;
        var transport=new FakeTelegramClient {ThrowOnSend=!completion,SendFailure=new IOException("test-token synthetic-private-text")};
        await Sut().TickAsync(Bot,transport,default); await Sut().TickAsync(Bot,transport,default);
        transport.Sent.Count.ShouldBe(completion ? 1 : 0); _store.Completed.ShouldBeEmpty();
        _logs.Lines.ShouldBe(["Reminder delivery outcome unknown: IOException"]);
    }

    [Fact]
    public async Task Transient_admission_failure_uses_fixed_retryable_wrapper_without_private_details()
    {
        _store.AdmissionFailure=new IOException("test-token synthetic-private-text");
        var ex=await Should.ThrowAsync<ReminderPersistenceException>(()=>Sut().AdmitAsync(Bot,Message(),false,default));
        ex.Message.ShouldBe("Reminder persistence failed."); ex.InnerException.ShouldBeNull(); _store.PreviewBegan.ShouldBeFalse();
    }

    [Theory]
    [InlineData("sent","sent","отправлено; последняя отправка: отправлено")]
    [InlineData("skipped","skipped","пропущено; последняя отправка: пропущено")]
    [InlineData("unknown","unknown","результат отправки неизвестен; последняя отправка: результат неизвестен (могла не дойти; автоматически не повторяется)")]
    public void List_item_uses_Russian_outcomes_without_database_identifier(string status,string outcome,string expected)
    {
        var row=_store.Item with {Status=status,LastOutcome=outcome};
        ReminderAssistant.ItemText(row).ShouldBe("synthetic task\n2026-01-02 12:10 UTC+00:00; один раз; "+expected);
        ReminderAssistant.ItemText(row).ShouldNotContain(row.Id.ToString("N"));
    }
}
