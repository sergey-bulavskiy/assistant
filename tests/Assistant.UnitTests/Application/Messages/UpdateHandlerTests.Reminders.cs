using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Application.Messages;

public partial class UpdateHandlerTests
{
    private sealed class ReminderRoute : IReminderAssistant
    {
        public bool Admitted; public bool Handled; public Exception? Failure;
        public Task<ReminderIntake?> AdmitAsync(ReceivingBot bot, IncomingMessage m,bool all,CancellationToken ct)
        {
            if(Failure!=null) throw Failure; Admitted=true;
            return Task.FromResult<ReminderIntake?>(new(new(bot.FamilyId!.Value,bot.BotDbId,bot.TelegramBotId,bot.Role,m.ChatId,m.TopicId,m.ChatType,m.UserId!.Value),"create",Guid.NewGuid()));
        }
        public Task HandleAsync(ReminderIntake i,ITelegramClient c,IncomingMessage m,StoreResult s,CancellationToken ct)
        { Handled=true; return c.SendTextAsync(m.ChatId,m.TopicId,"synthetic reminder preview",m.MessageId,ct); }
        public Task HandleCallbackAsync(ReceivingBot b,ITelegramClient c,CallbackQueryInfo q,CancellationToken ct)=>Task.CompletedTask;
        public Task TickAsync(ReceivingBot b,ITelegramClient c,CancellationToken ct)=>Task.CompletedTask;
    }
    private sealed class ReminderVetGuard : IVetAssistant
    {
        public int Admissions; public int Handled;
        public Task<VetAdmittedSource?> AdmitAsync(ReceivingBot b,IncomingMessage m,long update,CancellationToken ct)
        { Admissions++; return Task.FromResult<VetAdmittedSource?>(null); }
        public Task HandleAsync(ReceivingBot b,ITelegramClient c,IncomingMessage m,StoreResult s,VetAdmittedSource? a,CancellationToken ct,bool all=false)
        { Handled++; return Task.CompletedTask; }
        public Task ResumeAsync(ReceivingBot b,ITelegramClient c,CancellationToken ct)=>Task.CompletedTask;
        public Task HandleCallbackAsync(ReceivingBot b,ITelegramClient c,CallbackQueryInfo q,CancellationToken ct)=>Task.CompletedTask;
    }
    private static UpdateHandler ReminderHandler(FakeMessageStore store,ReminderRoute reminder,ReminderVetGuard vet,
        FakeGeneralAssistant general,FakeHealthAssistant health) => new(store,new FakeApprovalService(),new FakeCurrentFamily(),
        new FakeManagerUpdateHandler(),general,health,Options.Create(new BotOptions {ManagerToken="test-token"}),
        new BuildInfo("abcdef1",null,DateTimeOffset.Parse("2026-01-02T12:00:00Z")),
        new FixedClock(DateTimeOffset.Parse("2026-01-02T12:00:00Z")),NullLogger<UpdateHandler>.Instance,
        vetAssistant:vet,reminders:reminder);

    [Theory]
    [InlineData("general")]
    [InlineData("health")]
    [InlineData("vet")]
    public async Task Reminder_admission_precedes_offset_and_bypasses_all_role_interpretation(string role)
    {
        var reminder=new ReminderRoute(); var store=new FakeMessageStore(); var vet=new ReminderVetGuard();
        var general=new FakeGeneralAssistant(); var health=new FakeHealthAssistant(); var client=new FakeTelegramClient();
        store.BeforeStore=()=>{reminder.Admitted.ShouldBeTrue(); reminder.Handled.ShouldBeFalse();};
        var handler=ReminderHandler(store,reminder,vet,general,health);
        await handler.HandleAsync(GeneralBot with {Role=role},client,new(100,Message(text:"/remind in 10m synthetic task")),default);
        store.Calls.ShouldHaveSingleItem().Message!.Text.ShouldBe("/remind in 10m synthetic task");
        client.Sent.ShouldHaveSingleItem().Text.ShouldBe("synthetic reminder preview");
        general.Calls.ShouldBeEmpty(); health.Calls.ShouldBeEmpty(); vet.Admissions.ShouldBe(0); vet.Handled.ShouldBe(0);
    }

    [Fact]
    public async Task Reminder_admission_failure_does_not_advance_offset_or_enter_vet()
    {
        var reminder=new ReminderRoute {Failure=new ReminderPersistenceException()}; var store=new FakeMessageStore();
        var vet=new ReminderVetGuard(); var client=new FakeTelegramClient();
        var handler=ReminderHandler(store,reminder,vet,new(),new());
        await Should.ThrowAsync<ReminderPersistenceException>(()=>handler.HandleAsync(GeneralBot with {Role="vet"},client,new(100,Message(text:"/remind in 10m synthetic task")),default));
        store.Calls.ShouldBeEmpty(); client.Sent.ShouldBeEmpty(); vet.Admissions.ShouldBe(0); reminder.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task Reminder_offset_storage_failure_uses_fixed_wrapper_and_sends_no_preview()
    {
        var reminder=new ReminderRoute(); var store=new FakeMessageStore {ThrowOnStore=new IOException("test-token synthetic-private-text")};
        var client=new FakeTelegramClient(); var handler=ReminderHandler(store,reminder,new(),new(),new());
        var ex=await Should.ThrowAsync<ReminderPersistenceException>(()=>handler.HandleAsync(GeneralBot,client,new(100,Message(text:"/remind in 10m synthetic task")),default));
        ex.Message.ShouldBe("Reminder persistence failed."); ex.InnerException.ShouldBeNull();
        reminder.Admitted.ShouldBeTrue(); reminder.Handled.ShouldBeFalse(); client.Sent.ShouldBeEmpty(); store.Calls.ShouldBeEmpty();
    }
}
