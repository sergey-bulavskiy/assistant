using Assistant.Application.Health.Documents;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.UnitTests.Fakes;

namespace Assistant.UnitTests.Application.Messages;

public partial class UpdateHandlerTests
{
    private sealed partial class FakeHealthAssistant
    {
        public int DocumentAdmissions { get; private set; }
        public int DocumentRecoveryPasses { get; private set; }
        public Exception? AdmissionFailure { get; set; }
        public Action? OnDocumentAdmission { get; set; }
        public Task<HealthDocumentAdmissionInfo?> AdmitDocumentAsync(ReceivingBot bot, IncomingMessage message, long updateId, CancellationToken token)
        {
            DocumentAdmissions++;
            OnDocumentAdmission?.Invoke();
            if (AdmissionFailure is not null) throw AdmissionFailure;
            return Task.FromResult<HealthDocumentAdmissionInfo?>(null);
        }
        public Task ResumeDocumentsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken token)
        {
            DocumentRecoveryPasses++;
            return Task.CompletedTask;
        }
    }

    private static IncomingMessage FileMessage(string fileId = "synthetic-file", string name = "synthetic.docx") =>
        Message(chatType: "group", text: "synthetic caption") with
        { Kind = MessageKind.Document, TopicId = 7, Document = new(fileId, null, name, null, null) };

    [Fact]
    public async Task Health_document_admission_precedes_source_storage_even_for_unsupported_format()
    {
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        health.OnDocumentAdmission = () => store.Calls.ShouldBeEmpty();
        store.BeforeStore = () => health.DocumentAdmissions.ShouldBe(1);
        await handler.HandleAsync(HealthBot, telegram, new(101, FileMessage()), CancellationToken.None);
        health.DocumentAdmissions.ShouldBe(1);
        store.Calls.ShouldHaveSingleItem().Message!.Document!.FileName.ShouldBe("synthetic.docx");
        health.Calls.ShouldHaveSingleItem().Message.TopicId.ShouldBe(7);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_pre_offset_transient_admission_or_storage_has_fixed_safe_wrapper(bool admissionFailure)
    {
        var health = new FakeHealthAssistant();
        var store = new FakeMessageStore();
        var failure = new IOException("synthetic-private-sentinel");
        if (admissionFailure) health.AdmissionFailure = failure; else store.ThrowOnStore = failure;
        var (handler, _, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), store, health);
        var ex = await Should.ThrowAsync<HealthDocumentIntakePersistenceException>(() =>
            handler.HandleAsync(HealthBot, telegram, new(102, FileMessage()), CancellationToken.None));
        ex.Message.ShouldBe("Health document intake persistence failed.");
        ex.ToString().ShouldNotContain("synthetic-private-sentinel");
        store.Calls.ShouldBeEmpty();
        health.Calls.ShouldBeEmpty();
        telegram.Reactions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unauthorized_document_never_admits_or_dispatches(bool unapprovedMember)
    {
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        if (unapprovedMember) approvals.NextMemberStatus = FamilyMemberStatus.Pending;
        else approvals.NextPlaceStatus = PlaceStatus.Denied;
        await handler.HandleAsync(HealthBot, telegram, new(103, FileMessage()), CancellationToken.None);
        health.DocumentAdmissions.ShouldBe(0);
        health.Calls.ShouldBeEmpty();
        store.Calls.ShouldHaveSingleItem().Message.ShouldBeNull();
        telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_identity_is_not_admitted_or_given_transient_poison_exemption()
    {
        var health = new FakeHealthAssistant();
        var store = new FakeMessageStore { ThrowOnStore = new IOException("synthetic failure") };
        var (handler, _, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), store, health);
        await Should.ThrowAsync<IOException>(() => handler.HandleAsync(HealthBot, telegram, new(104, FileMessage(fileId: "")), CancellationToken.None));
        health.DocumentAdmissions.ShouldBe(0);
        health.Calls.ShouldBeEmpty();
        telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_and_never_advances_source()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var health = new FakeHealthAssistant { AdmissionFailure = new OperationCanceledException(cancellation.Token) };
        var (handler, store, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(HealthBot, telegram, new(105, FileMessage()), cancellation.Token));
        store.Calls.ShouldBeEmpty();
        health.Calls.ShouldBeEmpty();
        telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("general")]
    [InlineData("test")]
    public async Task Other_roles_never_use_health_document_admission_or_recovery(string role)
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        var bot = HealthBot with { Role = role };
        await handler.HandleAsync(bot, telegram, new(106, FileMessage()), CancellationToken.None);
        await handler.ResumeAsync(bot, telegram, CancellationToken.None);
        health.DocumentAdmissions.ShouldBe(0);
        health.DocumentRecoveryPasses.ShouldBe(0);
    }

    [Fact]
    public async Task Health_recovery_uses_its_typed_sibling()
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        await handler.ResumeAsync(HealthBot, telegram, CancellationToken.None);
        health.DocumentRecoveryPasses.ShouldBe(1);
        health.DocumentAdmissions.ShouldBe(0);
        health.Calls.ShouldBeEmpty();
    }
}
