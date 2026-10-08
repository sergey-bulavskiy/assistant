using Assistant.Application.Common;
using Assistant.Application.Expectations;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Application.Messages;

public partial class UpdateHandlerTests
{
    // Models only the application/store boundary. SQL authorization and profile existence
    // are exercised by the real-store integration tests.
    private sealed class ExpectationBoundaryStore : IExpectationStore
    {
        public readonly Guid Id = Guid.Parse("11111111111111111111111111111111");
        public readonly Guid DraftId = Guid.Parse("22222222222222222222222222222222");
        public List<(ReminderScope Scope, int Source, ExpectationCommand Command)> Commands { get; } = [];
        public List<(ReminderScope Scope, int MessageId, bool Save)> Resolutions { get; } = [];
        public ReminderScope? Owner { get; private set; }
        public int? BoundMessageId { get; private set; }
        public bool Saved { get; private set; }
        public string? AdmissionResult { get; init; }
        public Exception? ExecuteFailure { get; init; }
        public List<ReminderScope> Lists { get; } = [];
        public IReadOnlyList<ExpectationItem> Items { get; init; } = [];

        public Task<ExpectationIntake> ExecuteAsync(ReminderScope scope, int sourceMessageId,
            ExpectationCommand command, CancellationToken ct)
        {
            if (ExecuteFailure != null) throw ExecuteFailure;
            Commands.Add((scope, sourceMessageId, command));
            if (AdmissionResult != null)
                return Task.FromResult(new ExpectationIntake(scope, "result", Result: AdmissionResult));
            if (command.Kind is "create" or "edit" or "resume")
            {
                Owner = scope;
                return Task.FromResult(new ExpectationIntake(scope, "preview", Id, DraftId));
            }
            return Task.FromResult(new ExpectationIntake(scope, command.Kind, command.Id, Result: command.Kind));
        }

        public Task<ExpectationPreview?> BeginPreviewAsync(ReminderScope scope, Guid draftId, CancellationToken ct) =>
            Task.FromResult<ExpectationPreview?>(scope == Owner && draftId == DraftId
                ? new(Id, DraftId, "Synthetic profile", "glucose", 540, 30, 180,
                    new DateOnly(2026, 1, 3), new ReminderPreferences(180, 1320, 480)) : null);

        public Task BindPreviewAsync(ReminderScope scope, Guid draftId, int messageId, CancellationToken ct)
        {
            if (scope == Owner && draftId == DraftId) BoundMessageId = messageId;
            return Task.CompletedTask;
        }

        public Task<string> ResolveAsync(ReminderScope scope, Guid draftId, int messageId, bool save, CancellationToken ct)
        {
            Resolutions.Add((scope, messageId, save));
            if (scope != Owner || draftId != DraftId || messageId != BoundMessageId)
                return Task.FromResult("unavailable");
            Saved = save;
            return Task.FromResult(save ? "saved" : "cancelled");
        }

        public Task<IReadOnlyList<ExpectationItem>> ListAsync(ReminderScope scope, CancellationToken ct)
        {
            Lists.Add(scope);
            return Task.FromResult(Items);
        }
    }

    private static UpdateHandler ExpectationHandler(ExpectationBoundaryStore expectations,
        FakeMessageStore messages, FakeGeneralAssistant general, FakeHealthAssistant health,
        ReminderVetGuard vet, FakeApprovalService? approvals = null) =>
        new(messages, approvals ?? new FakeApprovalService(), new FakeCurrentFamily(),
            new FakeManagerUpdateHandler(), general, health,
            Options.Create(new BotOptions { ManagerToken = "test-token" }),
            new BuildInfo("abcdef1", null, DateTimeOffset.Parse("2026-01-02T12:00:00Z")),
            new FixedClock(DateTimeOffset.Parse("2026-01-02T12:00:00Z")),
            NullLogger<UpdateHandler>.Instance, vetAssistant: vet,
            expectations: new ExpectationAssistant(expectations, NullLogger<ExpectationAssistant>.Instance));

    [Theory]
    [InlineData("health", "private", null)]
    [InlineData("vet", "private", null)]
    [InlineData("health", "supergroup", 7)]
    [InlineData("vet", "supergroup", 7)]
    public async Task Expectation_create_preserves_exact_scope_and_bypasses_role_interpretation(
        string role, string chatType, int? topic)
    {
        var expected = new ExpectationBoundaryStore();
        var messages = new FakeMessageStore(); var general = new FakeGeneralAssistant();
        var health = new FakeHealthAssistant(); var vet = new ReminderVetGuard();
        var telegram = new FakeTelegramClient();
        var bot = GeneralBot with { Role = role };
        var message = Message(text: "/expect glucose daily 09:00 grace 30", chatType: chatType)
            with { ChatId = chatType == "private" ? 111 : -222, TopicId = topic };
        messages.BeforeStore = () => expected.Commands.Count.ShouldBe(1);

        await ExpectationHandler(expected, messages, general, health, vet)
            .HandleAsync(bot, telegram, new(100, message), default);

        var command = expected.Commands.ShouldHaveSingleItem();
        command.Scope.ShouldBe(new ReminderScope(42, 1, 999, role, message.ChatId, topic, chatType, 111));
        command.Source.ShouldBe(1);
        command.Command.ShouldBe(new ExpectationCommand("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30));
        messages.Calls.ShouldHaveSingleItem().Message.ShouldBe(message);
        var preview = telegram.ButtonMessages.ShouldHaveSingleItem();
        preview.ChatId.ShouldBe(message.ChatId); preview.TopicId.ShouldBe(topic);
        preview.ReplyToMessageId.ShouldBe(message.MessageId);
        preview.Text.ShouldContain("Synthetic profile"); preview.Text.ShouldContain("09:00");
        preview.Text.ShouldContain("09:30"); preview.Text.ShouldContain("UTC+03:00");
        preview.Text.ShouldContain("22:00"); preview.Text.ShouldContain("08:00");
        preview.Text.ShouldContain("2026-01-03");
        preview.Buttons.Select(b => b.CallbackData).ShouldBe(new[] { $"exp_save:{expected.DraftId:N}", $"exp_cancel:{expected.DraftId:N}" });
        expected.BoundMessageId.ShouldBe(preview.MessageId); expected.Saved.ShouldBeFalse();
        general.Calls.ShouldBeEmpty(); health.Calls.ShouldBeEmpty();
        vet.Admissions.ShouldBe(0); vet.Handled.ShouldBe(0);
    }

    [Theory]
    [InlineData("general", "/expect glucose daily 09:00 grace 30")]
    [InlineData("test", "/expect glucose daily 09:00 grace 30")]
    [InlineData("health", "/expect glucose daily 23:50 grace 30")]
    [InlineData("vet", "/expect weight daily 09:00 grace 30")]
    public async Task Rejected_expectation_command_returns_help_without_diary_interpretation(string role, string text)
    {
        var expected = new ExpectationBoundaryStore(); var messages = new FakeMessageStore();
        var general = new FakeGeneralAssistant(); var health = new FakeHealthAssistant();
        var vet = new ReminderVetGuard(); var telegram = new FakeTelegramClient();

        await ExpectationHandler(expected, messages, general, health, vet)
            .HandleAsync(GeneralBot with { Role = role }, telegram, new(100, Message(text: text)), default);

        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(ExpectationParser.Help);
        expected.Commands.ShouldBeEmpty(); expected.BoundMessageId.ShouldBeNull();
        general.Calls.ShouldBeEmpty(); health.Calls.ShouldBeEmpty(); vet.Admissions.ShouldBe(0); vet.Handled.ShouldBe(0);
    }

    [Fact]
    public async Task Expectation_command_addressed_to_another_bot_uses_normal_role_path()
    {
        var expected = new ExpectationBoundaryStore(); var health = new FakeHealthAssistant();
        var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, new(), new(), health, new())
            .HandleAsync(GeneralBot with { Role = "health" }, telegram,
                new(100, Message(text: "/expect@other_bot glucose daily 09:00 grace 30")), default);
        expected.Commands.ShouldBeEmpty(); telegram.Sent.ShouldBeEmpty();
        health.Calls.ShouldHaveSingleItem().Message.Text!.ShouldContain("@other_bot");
    }

    [Fact]
    public async Task Expectation_admission_failure_does_not_advance_offset_or_send_preview()
    {
        var expected = new ExpectationBoundaryStore { ExecuteFailure = new IOException("synthetic-private-text") };
        var messages = new FakeMessageStore(); var telegram = new FakeTelegramClient(); var health = new FakeHealthAssistant();
        var handler = ExpectationHandler(expected, messages, new(), health, new());
        var ex = await Should.ThrowAsync<ReminderPersistenceException>(() => handler.HandleAsync(
            GeneralBot with { Role = "health" }, telegram, new(100, Message(text: "/expectations")), default));
        ex.Message.ShouldBe("Reminder persistence failed."); ex.InnerException.ShouldBeNull();
        messages.Calls.ShouldBeEmpty(); telegram.Sent.ShouldBeEmpty(); health.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Expectation_offset_failure_keeps_admitted_draft_unbound()
    {
        var expected = new ExpectationBoundaryStore();
        var messages = new FakeMessageStore { ThrowOnStore = new IOException("synthetic-private-text") };
        var telegram = new FakeTelegramClient();
        await Should.ThrowAsync<ReminderPersistenceException>(() => ExpectationHandler(expected, messages, new(), new(), new())
            .HandleAsync(GeneralBot with { Role = "health" }, telegram,
                new(100, Message(text: "/expect glucose daily 09:00 grace 30")), default));
        expected.Commands.ShouldHaveSingleItem(); expected.BoundMessageId.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty(); messages.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_preview_send_never_binds_save_proof(bool cancelled)
    {
        var expected = new ExpectationBoundaryStore(); var messages = new FakeMessageStore();
        using var cancellation = new CancellationTokenSource();
        messages.BeforeStore = () => { if (cancelled) cancellation.Cancel(); };
        var telegram = new FakeTelegramClient { ThrowOnSend = true,
            SendFailure = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException("synthetic send failure") };
        var handler = ExpectationHandler(expected, messages, new(), new(), new());
        var operation = () => handler.HandleAsync(GeneralBot with { Role = "health" }, telegram,
            new(100, Message(text: "/expect glucose daily 09:00 grace 30")), cancellation.Token);
        if (cancelled) await Should.ThrowAsync<OperationCanceledException>(operation);
        else await operation();
        expected.BoundMessageId.ShouldBeNull(); expected.Saved.ShouldBeFalse();
        telegram.ButtonMessages.ShouldBeEmpty(); expected.Commands.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("expect_edit", " daily 10:00 grace 15", "edit")]
    [InlineData("expect_pause", "", "pause")]
    [InlineData("expect_resume", "", "resume")]
    [InlineData("expect_cancel", "", "cancel")]
    public async Task Management_command_passes_target_and_exact_topic_to_store(string command, string suffix, string kind)
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, new(), new(), new(), new()).HandleAsync(
            GeneralBot with { Role = "vet" }, telegram,
            new(100, Message(chatType: "supergroup", text: $"/{command} {expected.Id:N}{suffix}")
                with { ChatId = -222, TopicId = 7 }), default);
        var observed = expected.Commands.ShouldHaveSingleItem();
        observed.Command.Kind.ShouldBe(kind); observed.Command.Id.ShouldBe(expected.Id);
        observed.Scope.ShouldBe(new ReminderScope(42, 1, 999, "vet", -222, 7, "supergroup", 111));
        if (kind == "edit")
        { observed.Command.DeadlineMinute.ShouldBe(600); observed.Command.GraceMinutes.ShouldBe(15); }
        var reply = telegram.Sent.ShouldHaveSingleItem();
        if (kind is "edit" or "resume")
        {
            reply.Text.ShouldStartWith("Предварительный просмотр.");
            expected.BoundMessageId.ShouldBe(telegram.ButtonMessages.ShouldHaveSingleItem().MessageId);
        }
        else
        {
            reply.Text.ShouldBe(kind == "pause"
                ? "Проверка приостановлена. Возобновление требует нового подтверждения и начинается завтра."
                : "Проверка или предложение отменены.");
            telegram.ButtonMessages.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Expectation_list_renders_schedule_outcome_and_skipped_dates_in_current_scope()
    {
        var expected = new ExpectationBoundaryStore { Items = [new(
            Guid.Parse("33333333333333333333333333333333"), "Synthetic profile", "glucose", "active", 540, 30, 180,
            new DateOnly(2026, 1, 3), new DateOnly(2026, 1, 4), "dispatch-unknown", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6))] };
        var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, new(), new(), new(), new()).HandleAsync(
            GeneralBot with { Role = "health" }, telegram,
            new(100, Message(chatType: "supergroup", text: "/expectations") with { ChatId = -222, TopicId = 7 }), default);
        expected.Lists.ShouldHaveSingleItem().ShouldBe(new ReminderScope(42, 1, 999, "health", -222, 7, "supergroup", 111));
        var response = telegram.Sent.ShouldHaveSingleItem();
        response.Text.ShouldContain("33333333333333333333333333333333");
        response.Text.ShouldContain("Synthetic profile; glucose; active"); response.Text.ShouldContain("09:00");
        response.Text.ShouldContain("UTC+03:00"); response.Text.ShouldContain("dispatch-unknown");
        response.Text.ShouldContain("2026-01-05"); response.Text.ShouldContain("2026-01-06");
        response.ChatId.ShouldBe(-222); response.TopicId.ShouldBe(7); telegram.ButtonMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Profile_required_result_does_not_fall_through_to_profile_or_diary_creation()
    {
        var expected = new ExpectationBoundaryStore { AdmissionResult = "profile_required" };
        var health = new FakeHealthAssistant(); var vet = new ReminderVetGuard(); var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, new(), new(), health, vet).HandleAsync(
            GeneralBot with { Role = "health" }, telegram,
            new(100, Message(text: "/expect glucose daily 09:00 grace 30")), default);
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldContain("не создаёт профиль");
        telegram.ButtonMessages.ShouldBeEmpty(); expected.BoundMessageId.ShouldBeNull();
        health.Calls.ShouldBeEmpty(); vet.Admissions.ShouldBe(0);
    }

    [Theory]
    [InlineData("health", 111, -222, 7, true)]
    [InlineData("vet", 111, -222, 7, true)]
    [InlineData("health", 222, -222, 7, false)]
    [InlineData("health", 111, -333, 7, false)]
    [InlineData("health", 111, -222, 8, false)]
    public async Task Save_callback_routes_exact_creator_and_place_and_never_discloses_foreign_preview(
        string role, long user, long chat, int topic, bool authorized)
    {
        var expected = new ExpectationBoundaryStore(); var messages = new FakeMessageStore();
        var health = new FakeHealthAssistant(); var telegram = new FakeTelegramClient();
        var handler = ExpectationHandler(expected, messages, new(), health, new());
        var bot = GeneralBot with { Role = role };
        await handler.HandleAsync(bot, telegram, new(100,
            Message(chatType: "supergroup", text: "/expect glucose daily 09:00 grace 30")
                with { ChatId = -222, TopicId = 7 }), default);
        var preview = telegram.ButtonMessages.ShouldHaveSingleItem();
        await handler.HandleAsync(bot, telegram, new(101, null,
            new CallbackQueryInfo("synthetic-callback", user, $"exp_save:{expected.DraftId:N}", chat,
                preview.MessageId, topic, "supergroup")), default);

        expected.Resolutions.ShouldHaveSingleItem().Scope.ShouldBe(new ReminderScope(42, 1, 999, role, chat, topic, "supergroup", user));
        expected.Saved.ShouldBe(authorized); health.Callbacks.ShouldBeEmpty();
        var answer = telegram.AnsweredCallbacks.ShouldHaveSingleItem();
        answer.CallbackQueryId.ShouldBe("synthetic-callback");
        answer.Text.ShouldBe(authorized
            ? "Проверка сохранена. Начало или изменение — завтра в сохранённом смещении."
            : "Недоступно или уже решено.");
        telegram.Sent.Count.ShouldBe(1); messages.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_callback_for_unapproved_topic_never_reaches_expectation_store()
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        var approvals = new FakeApprovalService { FoundPlaceStatus = PlaceStatus.Pending };
        await ExpectationHandler(expected, new(), new(), new(), new(), approvals).HandleAsync(
            GeneralBot with { Role = "health" }, telegram, new(100, null,
                new CallbackQueryInfo("synthetic-callback", 111, $"exp_save:{expected.DraftId:N}", -222, 1, 7, "supergroup")), default);
        expected.Resolutions.ShouldBeEmpty(); expected.Saved.ShouldBeFalse();
        approvals.PlaceLookups.ShouldHaveSingleItem().ShouldBe((1L, -222L, (int?)7));
        telegram.AnsweredCallbacks.ShouldHaveSingleItem().Text.ShouldBe(UpdateHandler.NoRightsText);
        telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Preview_callback_requires_delivered_message_proof_and_cancel_is_distinct_from_save(
        bool wrongMessage, bool save)
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        var handler = ExpectationHandler(expected, new(), new(), new(), new());
        var bot = GeneralBot with { Role = "health" };
        await handler.HandleAsync(bot, telegram,
            new(100, Message(text: "/expect glucose daily 09:00 grace 30")), default);
        var previewId = telegram.ButtonMessages.ShouldHaveSingleItem().MessageId;
        var callbackMessageId = wrongMessage ? previewId + 1 : previewId;
        await handler.HandleAsync(bot, telegram, new(101, null,
            new CallbackQueryInfo("synthetic-callback", 111,
                $"{(save ? "exp_save" : "exp_cancel")}:{expected.DraftId:N}", 111,
                callbackMessageId, MessageChatType: "private")), default);
        var observed = expected.Resolutions.ShouldHaveSingleItem();
        observed.MessageId.ShouldBe(callbackMessageId); observed.Save.ShouldBe(save);
        expected.Saved.ShouldBe(save && !wrongMessage);
        telegram.AnsweredCallbacks.ShouldHaveSingleItem().Text.ShouldBe(wrongMessage
            ? "Недоступно или уже решено."
            : save ? "Проверка сохранена. Начало или изменение — завтра в сохранённом смещении."
            : "Проверка или предложение отменены.");
    }

    [Fact]
    public async Task Empty_expectation_list_returns_help_without_creating_a_preview()
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, new(), new(), new(), new()).HandleAsync(
            GeneralBot with { Role = "health" }, telegram, new(100, Message(text: "/expectations")), default);
        expected.Lists.ShouldHaveSingleItem();
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Проверок нет. " + ExpectationParser.Help);
        telegram.ButtonMessages.ShouldBeEmpty(); expected.BoundMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Edited_expectation_source_returns_management_guidance_without_mutating_schedule()
    {
        var expected = new ExpectationBoundaryStore(); var messages = new FakeMessageStore();
        messages.SetNextResult(new StoreResult(StoreOutcome.Updated, 1));
        var telegram = new FakeTelegramClient(); var health = new FakeHealthAssistant();
        await ExpectationHandler(expected, messages, new(), health, new()).HandleAsync(
            GeneralBot with { Role = "health" }, telegram,
            new(100, Message(text: "/expect glucose daily 10:00 grace 30") with { IsEdit = true }), default);
        expected.Commands.ShouldBeEmpty(); expected.BoundMessageId.ShouldBeNull(); health.Calls.ShouldBeEmpty();
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(
            "Изменение сообщения не меняет проверки. Используйте /expect_edit или /expect_cancel.");
    }

    [Theory]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    public async Task Redelivered_management_result_sends_no_duplicate_reply(StoreOutcome outcome)
    {
        var expected = new ExpectationBoundaryStore(); var messages = new FakeMessageStore();
        messages.SetNextResult(new StoreResult(outcome, 1));
        var telegram = new FakeTelegramClient();
        await ExpectationHandler(expected, messages, new(), new(), new()).HandleAsync(
            GeneralBot with { Role = "health" }, telegram,
            new(100, Message(text: $"/expect_pause {expected.Id:N}")), default);
        expected.Commands.ShouldHaveSingleItem().Command.Kind.ShouldBe("pause");
        telegram.Sent.ShouldBeEmpty(); telegram.ButtonMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 111, null)]
    [InlineData(111, 222, null)]
    [InlineData(111, 111, 7)]
    public async Task Invalid_private_creator_or_destination_is_consumed_without_store_admission(
        long userId, long chatId, int? topic)
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        var health = new FakeHealthAssistant();
        await ExpectationHandler(expected, new(), new(), health, new()).HandleAsync(
            GeneralBot with { Role = "health" }, telegram,
            new(100, Message(userId: userId, text: "/expect glucose daily 09:00 grace 30")
                with { ChatId = chatId, TopicId = topic }), default);
        expected.Commands.ShouldBeEmpty(); health.Calls.ShouldBeEmpty();
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Недоступно или уже решено.");
        telegram.ButtonMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("exp_save:invalid", 1, "health")]
    [InlineData("exp_save:22222222222222222222222222222222", 0, "health")]
    [InlineData("exp_save:22222222222222222222222222222222", 1, "general")]
    public async Task Malformed_or_unsupported_save_callback_returns_generic_answer_without_store_resolution(
        string data, int messageId, string role)
    {
        var expected = new ExpectationBoundaryStore(); var telegram = new FakeTelegramClient();
        var health = new FakeHealthAssistant();
        await ExpectationHandler(expected, new(), new(), health, new()).HandleAsync(
            GeneralBot with { Role = role }, telegram, new(100, null,
                new CallbackQueryInfo("synthetic-callback", 111, data, 111, messageId, MessageChatType: "private")), default);
        expected.Resolutions.ShouldBeEmpty(); health.Callbacks.ShouldBeEmpty();
        telegram.AnsweredCallbacks.ShouldHaveSingleItem().Text.ShouldBe("Недоступно или уже решено.");
        telegram.Sent.ShouldBeEmpty();
    }
}
