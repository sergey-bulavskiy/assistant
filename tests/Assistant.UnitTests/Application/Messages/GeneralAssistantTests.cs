using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Application.Messages;

public class GeneralAssistantTests
{
    private const long BotTelegramId = 999;
    private const long FamilyId = 42;
    private const long PrivateChatId = 111;
    private const long GroupChatId = -100;

    private static readonly ReceivingBot Bot = new(BotDbId: 1, TelegramBotId: BotTelegramId, Username: "test_bot", FamilyId: FamilyId, Role: "general");
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeMessageStore _store = new();
    private readonly FakeLlmGateway _gateway = new();
    private readonly FakeChatSettingsStore _chatSettings = new();
    private readonly FakeTelegramClient _telegram = new();
    private readonly BuildInfo _buildInfo = new("abcdef1234", null, Now.AddHours(-1));
    private readonly FixedClock _clock = new(Now);
    private long _nextUpdateId = 1;
    private int _nextMessageId = 100;

    private static LlmConfig Config(int maxContextMessages = 50, int maxInputChars = 10_000) => new()
    {
        Models = new[] { new ModelCatalogEntry("test", "sonnet"), new ModelCatalogEntry("test", "haiku") },
        CallsPerMinute = 10,
        CallsPerDay = 100,
        MaxContextMessages = maxContextMessages,
        MaxInputChars = maxInputChars,
        MaxOutputTokens = 1000,
        CallTimeoutSeconds = 60,
        MaxConcurrentCalls = 2,
        ModelCooldownMinutes = 5,
    };

    private GeneralAssistant CreateAssistant(LlmConfig? config = null, bool llmOff = false) =>
        new(_store, _gateway, _chatSettings, llmOff ? null : config ?? Config(), _clock, _buildInfo, NullLogger<GeneralAssistant>.Instance);

    private IncomingMessage Msg(
        string? text,
        string chatType = "private",
        long? chatId = null,
        int? topicId = null,
        int? replyToMessageId = null,
        long? replyToUserId = null,
        bool isEdit = false,
        MessageKind kind = MessageKind.Text,
        string? username = "test_user") =>
        new(
            ChatId: chatId ?? (chatType == "private" ? PrivateChatId : GroupChatId),
            ChatType: chatType,
            ChatTitle: chatType == "private" ? null : "test group",
            TopicId: topicId,
            MessageId: _nextMessageId++,
            UserId: 555,
            Username: username,
            Text: text,
            Kind: kind,
            IsEdit: isEdit,
            SentAt: Now,
            EditedAt: isEdit ? Now : null,
            MigrateToChatId: null,
            RawJson: "{}",
            ReplyToMessageId: replyToMessageId,
            ReplyToUserId: replyToUserId);

    /// <summary>Stores the message through the fake store (as UpdateHandler does) and hands it to
    /// the assistant with the real StoreResult.</summary>
    private async Task<StoreResult> HandleAsync(IncomingMessage message, GeneralAssistant? assistant = null, CancellationToken cancellationToken = default)
    {
        var result = await _store.StoreAsync(BotTelegramId, _nextUpdateId++, message, CancellationToken.None);
        await (assistant ?? CreateAssistant()).HandleAsync(Bot, _telegram, message, result, cancellationToken);
        return result;
    }

    // ---- Reply decision --------------------------------------------------------------------

    [Fact]
    public async Task Private_text_is_answered_and_the_reply_is_stored_as_outgoing()
    {
        await HandleAsync(Msg("test question"));

        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.ChatId.ShouldBe(PrivateChatId);
        sent.Text.ShouldBe("test answer");
        sent.ReplyToMessageId.ShouldBeNull();

        var stored = _store.OutgoingMessages.ShouldHaveSingleItem();
        stored.BotId.ShouldBe(BotTelegramId);
        stored.ChatId.ShouldBe(PrivateChatId);
        stored.ChatType.ShouldBe("private");
        stored.TelegramMessageId.ShouldBe(1);
        stored.Text.ShouldBe("test answer");
    }

    [Fact]
    public async Task Request_carries_family_bot_smart_tier_and_the_current_message_last()
    {
        await HandleAsync(Msg("test question"));

        var request = _gateway.LastRequest.ShouldNotBeNull();
        request.FamilyId.ShouldBe(FamilyId);
        request.BotId.ShouldBe(BotTelegramId);
        request.Tier.ShouldBe("smart");
        request.PreferredModel.ShouldBeNull();
        request.Messages.Last().ShouldBe(new LlmMessage(LlmMessageRole.User, "test question", null));
        request.Messages.Count(m => m.Text == "test question").ShouldBe(1);
        request.SystemPrompt.ShouldContain("2026-01-02");
    }

    [Fact]
    public async Task Group_request_system_prompt_mentions_several_people_and_messages_carry_authors()
    {
        await HandleAsync(Msg("@test_bot test question", chatType: "group", username: "test_user"));

        var request = _gateway.LastRequest.ShouldNotBeNull();
        request.SystemPrompt.ShouldContain("Several people");
        request.Messages.Last().Author.ShouldBe("test_user");
    }

    [Fact]
    public async Task Edited_message_is_not_answered()
    {
        await HandleAsync(Msg("test question", isEdit: true));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_message_is_not_answered()
    {
        await HandleAsync(Msg(null, kind: MessageKind.Service));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test caption")]
    public async Task Non_text_message_is_not_answered(string? caption)
    {
        await HandleAsync(Msg(caption, kind: MessageKind.Photo));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Redelivered_or_duplicate_updates_never_reach_the_gateway(StoreOutcome outcome)
    {
        var message = Msg("test question");

        await CreateAssistant().HandleAsync(Bot, _telegram, message, new StoreResult(outcome, 7), CancellationToken.None);

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _store.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Group_text_without_mention_or_reply_is_not_answered()
    {
        await HandleAsync(Msg("test question", chatType: "group"));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("@test_bot test question")]
    [InlineData("test question @TEST_BOT")]
    [InlineData("hey @Test_Bot, test question")]
    public async Task Group_text_mentioning_the_bot_is_answered_as_a_reply_to_the_trigger(string text)
    {
        var message = Msg(text, chatType: "supergroup");

        await HandleAsync(message);

        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.ChatId.ShouldBe(GroupChatId);
        sent.Text.ShouldBe("test answer");
        sent.ReplyToMessageId.ShouldBe(message.MessageId);
    }

    [Theory]
    [InlineData("@test_bot2 test question")]
    [InlineData("@test_bot_other test question")]
    [InlineData("test_bot test question")]
    public async Task Group_mention_of_a_longer_username_or_without_at_is_not_answered(string text)
    {
        await HandleAsync(Msg(text, chatType: "group"));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_email_like_mention_is_not_addressed()
    {
        // "me@test_bot" must never count as an @-mention (no word/'@' may precede the '@').
        await HandleAsync(Msg("contact me@test_bot please", chatType: "group"));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Group_reply_to_the_bots_own_message_is_answered()
    {
        var message = Msg("test question", chatType: "group", replyToMessageId: 50, replyToUserId: BotTelegramId);

        await HandleAsync(message);

        _telegram.Sent.ShouldHaveSingleItem().ReplyToMessageId.ShouldBe(message.MessageId);
    }

    [Fact]
    public async Task Group_reply_to_someone_else_is_not_answered()
    {
        await HandleAsync(Msg("test question", chatType: "group", replyToMessageId: 50, replyToUserId: 12345));

        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Forum_topic_root_reply_does_not_count_as_addressed()
    {
        // Telegram sets reply_to_message to the topic root on ordinary topic messages; even when the
        // root was sent by this bot, that is not a reply to the bot (spec 2.2).
        await HandleAsync(Msg("test question", chatType: "supergroup", topicId: 42, replyToMessageId: 42, replyToUserId: BotTelegramId));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Genuine_reply_to_the_bot_inside_a_forum_topic_is_answered_in_that_topic()
    {
        await HandleAsync(Msg("test question", chatType: "supergroup", topicId: 42, replyToMessageId: 77, replyToUserId: BotTelegramId));

        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.TopicId.ShouldBe(42);
        _store.OutgoingMessages.ShouldHaveSingleItem().TopicId.ShouldBe(42);
    }

    [Fact]
    public async Task Bot_without_family_is_ignored()
    {
        var message = Msg("test question");
        var manager = Bot with { FamilyId = null };

        await CreateAssistant().HandleAsync(manager, _telegram, message, new StoreResult(StoreOutcome.Stored, 1), CancellationToken.None);

        _telegram.Sent.ShouldBeEmpty();
    }

    // ---- Commands --------------------------------------------------------------------------

    [Fact]
    public async Task Start_in_private_chat_greets_without_calling_the_gateway()
    {
        await HandleAsync(Msg("/start"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldStartWith("Привет!");
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_in_a_group_is_silent()
    {
        // Spec 2.3 defines /start for private chats only; in groups it is treated like any other
        // unknown command.
        await HandleAsync(Msg("/start", chatType: "group"));

        _telegram.Sent.ShouldBeEmpty();
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task New_sets_the_context_cutoff_to_the_commands_own_message_id_and_confirms()
    {
        var result = await HandleAsync(Msg("/new"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Начинаем новый разговор.");
        _gateway.Requests.ShouldBeEmpty();
        var settings = await _chatSettings.GetAsync(FamilyId, BotTelegramId, PrivateChatId, null, CancellationToken.None);
        settings.ContextStartMessageId.ShouldNotBeNull();
        settings.ContextStartMessageId.ShouldBe(result.MessageDbId);
    }

    [Fact]
    public async Task New_with_no_stored_message_id_refuses_instead_of_claiming_success()
    {
        // StoreResult.MessageDbId is nullable; if the /new command's own message somehow has none,
        // the cutoff cannot be recorded, so the reply must be the Failed refusal text, never the
        // "new conversation started" confirmation.
        var message = Msg("/new");

        await CreateAssistant().HandleAsync(Bot, _telegram, message, new StoreResult(StoreOutcome.Stored, null), CancellationToken.None);

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Не получилось ответить, попробуйте ещё раз.");
        var settings = await _chatSettings.GetAsync(FamilyId, BotTelegramId, PrivateChatId, null, CancellationToken.None);
        settings.ContextStartMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task New_cutoff_removes_earlier_turns_from_the_next_requests_context()
    {
        await HandleAsync(Msg("test old question"));
        await HandleAsync(Msg("/new"));
        await HandleAsync(Msg("test new question"));

        var request = _gateway.LastRequest.ShouldNotBeNull();
        request.Messages.ShouldHaveSingleItem().Text.ShouldBe("test new question");
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/new")]
    [InlineData("/model")]
    [InlineData("/version")]
    public async Task Command_replies_are_sent_but_not_stored_as_context(string command)
    {
        await HandleAsync(Msg(command));

        _telegram.Sent.ShouldHaveSingleItem();
        _store.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task New_in_a_group_is_handled_without_mention()
    {
        await HandleAsync(Msg("/new", chatType: "group"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Начинаем новый разговор.");
    }

    [Fact]
    public async Task Outgoing_parts_become_context_for_the_next_turn()
    {
        await HandleAsync(Msg("test first question"));
        _gateway.NextResult = LlmResult.Answered("test second answer", "sonnet");
        await HandleAsync(Msg("test second question"));

        var request = _gateway.LastRequest.ShouldNotBeNull();
        request.Messages.Select(m => (m.Role, m.Text)).ShouldBe(new[]
        {
            (LlmMessageRole.User, "test first question"),
            (LlmMessageRole.Assistant, "test answer"),
            (LlmMessageRole.User, "test second question"),
        });
    }

    [Fact]
    public async Task Model_without_argument_lists_models_with_availability_and_the_current_choice()
    {
        _gateway.Models = new()
        {
            new ModelStatus("sonnet", true, null),
            new ModelStatus("haiku", false, new DateTimeOffset(2026, 1, 2, 5, 30, 0, TimeSpan.Zero)),
            new ModelStatus("opus", false, null),
        };

        await HandleAsync(Msg("/model"));

        var text = _telegram.Sent.ShouldHaveSingleItem().Text;
        text.ShouldContain("sonnet: доступна");
        text.ShouldContain("haiku: недоступна до 05:30 UTC");
        text.ShouldContain("opus: недоступна");
        text.ShouldNotContain("до  UTC");
        text.ShouldContain("auto");
        text.ShouldNotContain("(текущая)");
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Model_with_a_known_name_sets_the_preference_used_by_the_next_request()
    {
        await HandleAsync(Msg("/model HAIKU"));

        var text = _telegram.Sent.ShouldHaveSingleItem().Text;
        text.ShouldContain("haiku: доступна (текущая)");
        (await _chatSettings.GetAsync(FamilyId, BotTelegramId, PrivateChatId, null, CancellationToken.None)).PreferredModel.ShouldBe("haiku");

        await HandleAsync(Msg("test question"));
        _gateway.LastRequest.ShouldNotBeNull().PreferredModel.ShouldBe("haiku");
    }

    [Fact]
    public async Task Model_preference_is_per_chat_and_topic()
    {
        await HandleAsync(Msg("/model haiku", chatType: "supergroup", topicId: 7));

        await HandleAsync(Msg("@test_bot test question", chatType: "supergroup", topicId: 8));
        _gateway.LastRequest.ShouldNotBeNull().PreferredModel.ShouldBeNull();

        await HandleAsync(Msg("@test_bot test question", chatType: "supergroup", topicId: 7));
        _gateway.LastRequest.ShouldNotBeNull().PreferredModel.ShouldBe("haiku");

        await HandleAsync(Msg("test question"));
        _gateway.LastRequest.ShouldNotBeNull().PreferredModel.ShouldBeNull();
    }

    [Fact]
    public async Task Model_with_an_unknown_name_lists_again_and_keeps_the_preference()
    {
        await HandleAsync(Msg("/model haiku"));

        await HandleAsync(Msg("/model does-not-exist"));

        _telegram.Sent.Count.ShouldBe(2);
        _telegram.Sent[1].Text.ShouldContain("haiku: доступна (текущая)");
        _telegram.Sent[1].Text.ShouldContain("sonnet");
        (await _chatSettings.GetAsync(FamilyId, BotTelegramId, PrivateChatId, null, CancellationToken.None)).PreferredModel.ShouldBe("haiku");
    }

    [Fact]
    public async Task Model_auto_clears_the_preference()
    {
        await HandleAsync(Msg("/model haiku"));

        await HandleAsync(Msg("/model auto"));

        (await _chatSettings.GetAsync(FamilyId, BotTelegramId, PrivateChatId, null, CancellationToken.None)).PreferredModel.ShouldBeNull();
        _telegram.Sent[1].Text.ShouldNotContain("(текущая)");
    }

    [Fact]
    public async Task Model_with_llm_off_replies_not_configured()
    {
        _gateway.Models = new();

        await HandleAsync(Msg("/model"), CreateAssistant(llmOff: true));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Ассистент пока не настроен.");
    }

    [Fact]
    public async Task Version_replies_with_the_shared_version_text()
    {
        await HandleAsync(Msg("/version"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(VersionText.Format(_buildInfo, Now));
        _gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("private", "/frobnicate")]
    [InlineData("group", "/frobnicate")]
    [InlineData("group", "/new@other_bot")]
    [InlineData("private", "/model@other_bot haiku")]
    public async Task Unknown_commands_and_commands_for_other_bots_are_silent(string chatType, string text)
    {
        await HandleAsync(Msg(text, chatType: chatType));

        _telegram.Sent.ShouldBeEmpty();
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Command_addressed_to_this_bot_in_a_group_is_handled()
    {
        await HandleAsync(Msg("/version@test_bot", chatType: "group"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(VersionText.Format(_buildInfo, Now));
    }

    // ---- Refusals and LLM off --------------------------------------------------------------

    [Theory]
    [InlineData(LlmRefusalReason.RateLimited, "Слишком много запросов, подождите минуту.")]
    [InlineData(LlmRefusalReason.DailyCapReached, "Дневной лимит запросов исчерпан, продолжим завтра.")]
    [InlineData(LlmRefusalReason.AllModelsUnavailable, "Все модели сейчас недоступны (лимиты), попробуйте позже.")]
    [InlineData(LlmRefusalReason.Failed, "Не получилось ответить, попробуйте ещё раз.")]
    [InlineData(LlmRefusalReason.NotConfigured, "Ассистент пока не настроен.")]
    public async Task Refusals_reply_with_the_spec_text(LlmRefusalReason reason, string expected)
    {
        _gateway.NextResult = LlmResult.Refused(reason);

        await HandleAsync(Msg("test question"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(expected);
        _store.OutgoingMessages.ShouldBeEmpty(); // refusal texts are not conversation context
    }

    [Fact]
    public async Task All_models_unavailable_with_a_retry_time_names_it()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.AllModelsUnavailable, new DateTimeOffset(2026, 1, 2, 14, 5, 0, TimeSpan.Zero));

        await HandleAsync(Msg("test question"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Все модели сейчас недоступны (лимиты), попробуйте позже. Не раньше 14:05 UTC.");
    }

    [Fact]
    public async Task Llm_off_replies_not_configured_without_calling_the_gateway()
    {
        await HandleAsync(Msg("test question"), CreateAssistant(llmOff: true));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Ассистент пока не настроен.");
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Llm_off_in_a_group_stays_silent_when_not_addressed()
    {
        await HandleAsync(Msg("test question", chatType: "group"), CreateAssistant(llmOff: true));

        _telegram.Sent.ShouldBeEmpty();
    }

    // ---- Typing, splitting, delivery failures ---------------------------------------------

    [Fact]
    public async Task Typing_is_sent_while_waiting_and_stops_when_the_call_ends()
    {
        var answerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.WaitBeforeAnswering = answerGate.Task;
        var message = Msg("test question");
        var result = await _store.StoreAsync(BotTelegramId, _nextUpdateId++, message, CancellationToken.None);

        var handling = CreateAssistant().HandleAsync(Bot, _telegram, message, result, CancellationToken.None);

        _telegram.ChatActionsSent.ShouldNotBeEmpty();
        _telegram.ChatActionsSent.ShouldAllBe(a => a.Action == "typing" && a.ChatId == PrivateChatId);
        _telegram.Sent.ShouldBeEmpty();

        answerGate.SetResult();
        await handling;

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("test answer");
        var actionsAfterAnswer = _telegram.ChatActionsSent.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        _telegram.ChatActionsSent.Count.ShouldBe(actionsAfterAnswer);
    }

    [Fact]
    public async Task Typing_failures_are_ignored()
    {
        _telegram.ThrowOnChatAction = true;

        await Should.NotThrowAsync(() => HandleAsync(Msg("test question")));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("test answer");
    }

    [Fact]
    public async Task Long_answer_is_split_and_every_part_is_sent_and_stored()
    {
        var paragraph = new string('a', 3000);
        var answer = $"{paragraph}\n\n{paragraph}\n\n{paragraph}";
        _gateway.NextResult = LlmResult.Answered(answer, "sonnet");
        var message = Msg("@test_bot test question", chatType: "group");

        await HandleAsync(message);

        var expectedParts = ReplySplitter.Split(answer);
        expectedParts.Count.ShouldBeGreaterThan(1);
        _telegram.Sent.Select(s => s.Text).ShouldBe(expectedParts);
        _store.OutgoingMessages.Select(s => s.Text).ShouldBe(expectedParts);
        _store.OutgoingMessages.Select(s => s.TelegramMessageId).ShouldBe(Enumerable.Range(1, expectedParts.Count));
        _telegram.Sent[0].ReplyToMessageId.ShouldBe(message.MessageId);
        _telegram.Sent.Skip(1).ShouldAllBe(s => s.ReplyToMessageId == null);
    }

    [Fact]
    public async Task Outgoing_store_uses_no_cancellation_and_its_failure_does_not_stop_delivery()
    {
        var paragraph = new string('a', 3000);
        _gateway.NextResult = LlmResult.Answered($"{paragraph}\n\n{paragraph}", "sonnet");
        _store.ThrowOnStoreOutgoing = true;
        using var cts = new CancellationTokenSource();

        await Should.NotThrowAsync(() => HandleAsync(Msg("test question"), cancellationToken: cts.Token));

        _telegram.Sent.Count.ShouldBe(2);
        _store.OutgoingTokens.Count.ShouldBe(2);
        _store.OutgoingTokens.ShouldAllBe(t => !t.CanBeCanceled);
    }

    [Fact]
    public async Task Send_failure_stops_remaining_parts_and_does_not_throw()
    {
        var paragraph = new string('a', 3000);
        _gateway.NextResult = LlmResult.Answered($"{paragraph}\n\n{paragraph}\n\n{paragraph}", "sonnet");
        _telegram.ThrowOnSendNumber = 2;

        await Should.NotThrowAsync(() => HandleAsync(Msg("test question")));

        _telegram.Sent.Count.ShouldBe(1);
        _store.OutgoingMessages.Count.ShouldBe(1);
    }
}
