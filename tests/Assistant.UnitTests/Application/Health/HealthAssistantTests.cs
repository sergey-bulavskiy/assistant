using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging;

namespace Assistant.UnitTests.Application.Health;

public class HealthAssistantTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-02-07T10:00:00Z");
    private static readonly ReceivingBot Bot = new(BotDbId: 1, TelegramBotId: 999, Username: "test_health_bot", FamilyId: 42, Role: "health");

    private const string OwnerOnly = "Только владелец семьи может менять профиль.";

    private readonly FakeHealthProfileStore _profiles = new();
    private readonly FakeFamilyOwnership _ownership = new();
    private readonly FakeTelegramClient _telegram = new();
    private readonly FakeEventStore _events = new();
    private readonly FakeSafetyAlertStore _alerts = new();
    private readonly FakeMessageStore _messages = new();
    private readonly FakeLlmGateway _gateway = new() { NextResult = LlmResult.Answered(NoEventsJson, "haiku") };
    private readonly FakeRolePrompts _prompts = new();
    private readonly FailureNoticeThrottle _throttle = new();
    private readonly AddressedHintThrottle _hints = new();
    private readonly CapturingLogger _log = new();
    private int _nextMessageId = 100;

    private const string NoEventsJson = "{\"events\":[],\"unclear\":[],\"is_question\":false}";

    private const string GlucoseAt930Json =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":\"09:30\",\"value\":7.8,\"unit\":\"mmol/L\",\"context\":\"after_meal_1h\"}]," +
        "\"unclear\":[],\"is_question\":false}";

    public HealthAssistantTests()
    {
        _ownership.OwnerUserIds.Add(111);
    }

    private static LlmConfig Config() => new()
    {
        Models = new[] { new ModelCatalogEntry("test", "sonnet"), new ModelCatalogEntry("test", "haiku") },
        CallsPerMinute = 10,
        CallsPerDay = 100,
        MaxContextMessages = 30,
        MaxInputChars = 8000,
        MaxOutputTokens = 1000,
        CallTimeoutSeconds = 60,
        MaxConcurrentCalls = 2,
        ModelCooldownMinutes = 5,
        Prices = new Dictionary<string, ModelPrice>(),
        Budget = null,
        FastModels = Array.Empty<ModelCatalogEntry>(),
    };

    private HealthAssistant CreateAssistant(IClock? clock = null, bool llmOff = false) =>
        new(_profiles, _ownership, _events, _alerts, _messages, _gateway, llmOff ? null : Config(), _prompts, _throttle, _hints,
            clock ?? new FixedClock(Now), new BuildInfo("abcdef1234", null, Now.AddHours(-1)), _log);

    private IncomingMessage Msg(
        string? text, string chatType = "private", long userId = 111, int? topicId = null, MessageKind kind = MessageKind.Text,
        int? replyToMessageId = null, long? replyToUserId = null) =>
        new(ChatId: chatType == "private" ? userId : -100, ChatType: chatType, ChatTitle: chatType == "private" ? null : "test group",
            TopicId: topicId, MessageId: _nextMessageId++, UserId: userId, Username: "test_user",
            Text: text, Kind: kind, IsEdit: false, SentAt: Now, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: replyToMessageId, ReplyToUserId: replyToUserId);

    private void Answer(string json) => _gateway.NextResult = LlmResult.Answered(json, "haiku");

    private Task HandleAsync(IncomingMessage message, StoreOutcome outcome = StoreOutcome.Stored, IClock? clock = null, long messageDbId = 1) =>
        CreateAssistant(clock).HandleAsync(Bot, _telegram, message, new StoreResult(outcome, messageDbId), CancellationToken.None);

    private string SingleReply() => _telegram.Sent.ShouldHaveSingleItem().Text;

    [Theory]
    [InlineData(StoreOutcome.Updated)]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Only_new_messages_are_handled(StoreOutcome outcome)
    {
        await HandleAsync(Msg("/week"), outcome);
        Answer(GlucoseAt930Json);
        await HandleAsync(Msg("сахар 7.8 в 9:30"), outcome);

        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(0);
        _gateway.Requests.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public async Task Plain_text_creates_the_profile_and_gets_no_reply_unless_addressed(string chatType)
    {
        await HandleAsync(Msg("test message", chatType));

        // A private chat is always addressed: it gets the one-time hint (tests below); a group stays silent.
        _telegram.Sent.Select(s => s.Text).ShouldBe(chatType == "private" ? new[] { Hint } : Array.Empty<string>());
        _profiles.GetOrCreateCalls.ShouldBe(1);
        _profiles.LastFamilyId.ShouldBe(42);
        _profiles.LastBotDbId.ShouldBe(1);
    }

    [Fact]
    public async Task Week_without_a_start_date_says_it_is_not_set()
    {
        await HandleAsync(Msg("/week"));

        SingleReply().ShouldBe("Неделя: не задана (/setstart)");
    }

    [Fact]
    public async Task Week_counts_from_the_start_date()
    {
        _profiles.Profile = _profiles.Profile with { StageStartDate = new DateOnly(2030, 1, 15) };

        await HandleAsync(Msg("/week"));

        SingleReply().ShouldBe("Неделя: 3 нед. 2 дн.");
    }

    [Fact]
    public async Task Week_uses_the_profiles_time_zone()
    {
        // 22:30 UTC on Feb 7 is already Feb 8 in Tokyo.
        _profiles.Profile = _profiles.Profile with { StageStartDate = new DateOnly(2030, 1, 15), TimeZone = "Asia/Tokyo" };

        await HandleAsync(Msg("/week"), clock: new FixedClock(DateTimeOffset.Parse("2030-02-07T22:30:00Z")));

        SingleReply().ShouldBe("Неделя: 3 нед. 3 дн.");
    }

    [Fact]
    public async Task Week_out_of_range_asks_to_check_the_date()
    {
        _profiles.Profile = _profiles.Profile with { StageStartDate = new DateOnly(2029, 4, 12) };

        await HandleAsync(Msg("/week"));

        SingleReply().ShouldBe("Неделя: не определена — проверьте дату (/setstart)");
    }

    [Fact]
    public async Task Group_command_is_a_reply_in_the_same_topic()
    {
        var groupMessage = Msg("/week@test_health_bot", "group", topicId: 7);

        await HandleAsync(groupMessage);

        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.ChatId.ShouldBe(-100);
        sent.TopicId.ShouldBe(7);
        sent.ReplyToMessageId.ShouldBe(groupMessage.MessageId);

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/week"));
        _telegram.Sent.ShouldHaveSingleItem().ReplyToMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Profile_shows_every_field()
    {
        await HandleAsync(Msg("/profile"));

        SingleReply().ShouldBe(
            "Профиль:\nНачало отсчёта: не задано (/setstart)\nНеделя: не задана (/setstart)\nЧасовой пояс: UTC\n" +
            "Телефон для экстренных случаев: 103 или 112\n" +
            "Заметка: не задана (/setnote) — без неё ответы на вопросы не знают контекста\n" +
            "Пороги: правил 11, от врача 0 (/thresholds)");

        _telegram.Sent.Clear();
        _profiles.Profile = new HealthProfileInfo(1, new DateOnly(2030, 1, 15), "Europe/Berlin", "112", "test note");
        var index = _profiles.Rules.FindIndex(r => r.RuleKey == "glucose.any");
        _profiles.Rules[index] = _profiles.Rules[index] with { Source = SafetyRuleSources.Doctor };

        await HandleAsync(Msg("/profile"));

        var lines = SingleReply().Split('\n');
        lines.ShouldContain("Начало отсчёта: 15.01.2030");
        lines.ShouldContain("Неделя: 3 нед. 2 дн.");
        lines.ShouldContain("Часовой пояс: Europe/Berlin");
        lines.ShouldContain("Телефон для экстренных случаев: 112");
        lines.ShouldContain("Заметка: test note");
        lines.ShouldContain("Пороги: правил 11, от врача 1 (/thresholds)");
    }

    [Fact]
    public async Task Thresholds_lists_every_rule_with_its_source()
    {
        await HandleAsync(Msg("/thresholds"));

        var reply = SingleReply();
        var lines = reply.Split('\n');
        lines[0].ShouldBe("Пороги (глюкоза в ммоль/л, давление в мм рт. ст.):");
        lines.ShouldContain("glucose.any: low_urgent 3.0, low_alert 3.9, high_alert 11.0, high_urgent 13.9 — не подтверждено врачом");
        lines.ShouldContain("blood_pressure.diastolic: high_alert 90, high_urgent 110 — не подтверждено врачом");
        lines.ShouldContain("symptom.bleeding: symptom_level urgent — не подтверждено врачом");
        lines.ShouldContain("combo.bp_symptoms: window_hours 24 — не подтверждено врачом");
        lines.Count(l => l.EndsWith("— не подтверждено врачом")).ShouldBe(11);
        reply.ShouldEndWith("Вернуть по умолчанию: /threshold <правило> default.");

        _telegram.Sent.Clear();
        var index = _profiles.Rules.FindIndex(r => r.RuleKey == "glucose.fasting");
        _profiles.Rules[index] = _profiles.Rules[index] with { Source = SafetyRuleSources.Doctor };

        await HandleAsync(Msg("/thresholds"));

        SingleReply().Split('\n').ShouldContain("glucose.fasting: target_high 5.1 — врач");
    }

    [Fact]
    public async Task Start_in_private_lists_commands_and_limits()
    {
        await HandleAsync(Msg("/start"));

        var reply = SingleReply();
        foreach (var expected in new[]
                 {
                     "/today", "/undo", "/del", "/week", "/profile", "/thresholds", "/setstart", "/threshold", "не заменяю врача",
                     "не подтверждено врачом", "не советую лекарства", "упомяните меня"
                 })
        {
            reply.ShouldContain(expected);
        }

        reply.ShouldNotContain("не проверяю");

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/start", "group"));
        _telegram.Sent.ShouldBeEmpty();
    }

    private const string DeleteUsage =
        "Формат: /del в ответ на сообщение с показателями или /del <номер записи> (номера — в /today).";

    private static HealthEventInfo GlucoseEvent(long id) =>
        new(id, "glucose", Now, "{\"value\":7.8,\"context\":\"after_meal_1h\"}", null);

    private static HealthEventInfo WeightEvent(long id) => new(id, "weight", Now, "{\"kg\":64.5}", null);

    [Fact]
    public async Task Today_lists_todays_events_in_local_time()
    {
        _profiles.Profile = _profiles.Profile with { TimeZone = "Europe/Berlin" };
        _events.ActiveEvents.Add(new HealthEventInfo(12, "glucose", DateTimeOffset.Parse("2030-02-07T08:30:00Z"),
            "{\"value\":7.8,\"context\":\"after_meal_1h\"}", null));
        _events.ActiveEvents.Add(new HealthEventInfo(13, "meal", DateTimeOffset.Parse("2030-02-07T09:15:00Z"),
            "{\"meal_kind\":\"lunch\",\"description\":\"гречка\"}", null));
        _events.ActiveEvents.Add(new HealthEventInfo(14, "blood_pressure", DateTimeOffset.Parse("2030-02-07T09:40:00Z"),
            "{\"systolic\":128,\"diastolic\":84,\"pulse\":76}", null));
        _events.ActiveEvents.Add(new HealthEventInfo(15, "weight", DateTimeOffset.Parse("2030-02-07T09:55:00Z"), "{\"kg\":64.5}", null));

        await HandleAsync(Msg("/today"));

        SingleReply().ShouldBe(
            "Сегодня, 07.02.2030:\n#12 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)\n#13 10:15 обед: гречка\n" +
            "#14 10:40 давление 128/84, пульс 76\n#15 10:55 вес 64.5 кг");
        _events.LastRange.ShouldBe((42L, 1L, DateTimeOffset.Parse("2030-02-06T23:00:00Z"), DateTimeOffset.Parse("2030-02-07T23:00:00Z")));
        _gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Today_without_events()
    {
        await HandleAsync(Msg("/today"));

        SingleReply().ShouldBe("Сегодня записей нет.");
    }

    [Fact]
    public async Task Undo_deletes_the_senders_latest_and_clears_the_reaction()
    {
        _events.NextDeleted = new DeletedEvents(new[] { GlucoseEvent(12) }, new[] { new MessageRef(-100, 100) });

        await HandleAsync(Msg("/undo", "group", topicId: 7));

        var call = _events.DeleteCalls.ShouldHaveSingleItem();
        call.ShouldBe(new FakeEventStore.DeleteCall(
            "latest", 42, 1, 999, -100, 7, 111, DateTimeOffset.Parse("2030-02-06T10:00:00Z"), null, null, "undo"));
        _telegram.Reactions.ShouldBe(new[] { (-100L, 100, (string?)null) });
        SingleReply().ShouldBe("Удалено: #12 глюкоза 7.8 ммоль/л (через 1 ч после еды).");
    }

    [Fact]
    public async Task Undo_with_nothing_to_undo()
    {
        await HandleAsync(Msg("/undo"));

        SingleReply().ShouldBe("Нечего отменять.");
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Del_as_a_reply_deletes_that_messages_events()
    {
        _events.NextDeleted = new DeletedEvents(new[] { GlucoseEvent(12), WeightEvent(13) }, new[] { new MessageRef(-100, 55) });

        await HandleAsync(Msg("/del", "group", replyToMessageId: 55));

        var call = _events.DeleteCalls.ShouldHaveSingleItem();
        call.Kind.ShouldBe("message");
        call.BotId.ShouldBe(999);
        call.ChatId.ShouldBe(-100);
        call.TelegramMessageId.ShouldBe(55);
        call.Reason.ShouldBe("del");
        _telegram.Reactions.ShouldBe(new[] { (-100L, 55, (string?)null) });
        SingleReply().ShouldBe("Удалено: #12 глюкоза 7.8 ммоль/л (через 1 ч после еды); #13 вес 64.5 кг.");
    }

    [Fact]
    public async Task Del_reply_to_the_topic_root_is_not_a_reply()
    {
        await HandleAsync(Msg("/del", "group", topicId: 7, replyToMessageId: 7));

        SingleReply().ShouldBe(DeleteUsage);
        _events.DeleteCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/del 12")]
    [InlineData("/del #12")]
    public async Task Del_with_an_id(string text)
    {
        _events.NextDeleted = new DeletedEvents(new[] { GlucoseEvent(12) }, Array.Empty<MessageRef>());

        await HandleAsync(Msg(text));

        var call = _events.DeleteCalls.ShouldHaveSingleItem();
        call.Kind.ShouldBe("id");
        call.EventId.ShouldBe(12);
        call.Reason.ShouldBe("del");
        SingleReply().ShouldStartWith("Удалено: #12 ");
    }

    [Fact]
    public async Task Del_not_found()
    {
        await HandleAsync(Msg("/del 99"));

        SingleReply().ShouldBe("Не нашёл такую запись.");
    }

    [Theory]
    [InlineData("/del")]
    [InlineData("/del abc")]
    [InlineData("/del 0")]
    [InlineData("/del -5")]
    [InlineData("/del 12 13")]
    public async Task Del_with_bad_arguments_gets_the_usage(string text)
    {
        await HandleAsync(Msg(text));

        SingleReply().ShouldBe(DeleteUsage);
        _events.DeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Version_replies_with_the_running_version()
    {
        await HandleAsync(Msg("/version"));

        SingleReply().ShouldStartWith("abcdef1 · built");
    }

    [Theory]
    [InlineData("/frobnicate")]
    [InlineData("/week@other_bot")]
    public async Task Unknown_commands_and_commands_for_other_bots_are_silent(string text)
    {
        await HandleAsync(Msg(text));

        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_text_in_private_gets_the_text_only_note()
    {
        const string note = "Голосовые и фото пока не поддерживаются — напишите текстом.";

        await HandleAsync(Msg(null, kind: MessageKind.Photo));
        await HandleAsync(Msg(null, kind: MessageKind.Voice));
        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { note, note });

        _telegram.Sent.Clear();
        await HandleAsync(Msg(null, "group", kind: MessageKind.Photo));
        await HandleAsync(Msg(null, kind: MessageKind.Service));
        _telegram.Sent.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_send_does_not_fail_the_update()
    {
        _telegram.ThrowOnSend = true;

        await Should.NotThrowAsync(() => HandleAsync(Msg("/week")));

        _profiles.GetOrCreateCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("/setstart 15.01.2030")]
    [InlineData("/settz Europe/Berlin")]
    [InlineData("/setphone 112")]
    [InlineData("/setnote test note")]
    [InlineData("/setstart")]
    public async Task Non_owner_cannot_change_the_profile(string text)
    {
        var before = _profiles.Profile;

        await HandleAsync(Msg(text, userId: 222));

        SingleReply().ShouldBe(OwnerOnly);
        _profiles.Profile.ShouldBe(before);
        _profiles.LastUpdatedByUserId.ShouldBeNull();
    }

    [Fact]
    public async Task Setstart_saves_the_date_and_shows_the_week()
    {
        await HandleAsync(Msg("/setstart 15.01.2030"));

        _profiles.Profile.StageStartDate.ShouldBe(new DateOnly(2030, 1, 15));
        _profiles.LastUpdatedByUserId.ShouldBe(111);
        SingleReply().ShouldBe("Начало отсчёта: 15.01.2030. Неделя: 3 нед. 2 дн.");

        await HandleAsync(Msg("/setstart 5.1.2030"));
        _profiles.Profile.StageStartDate.ShouldBe(new DateOnly(2030, 1, 5));

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/setstart 13.04.2029"));
        _profiles.Profile.StageStartDate.ShouldBe(new DateOnly(2029, 4, 13));
        SingleReply().ShouldEndWith("Неделя: 42 нед. 6 дн.");
    }

    [Theory]
    [InlineData("/setstart", "Укажите дату начала отсчёта: /setstart ДД.ММ.ГГГГ")]
    [InlineData("/setstart 2030-01-15", "Укажите дату начала отсчёта: /setstart ДД.ММ.ГГГГ")]
    [InlineData("/setstart 32.01.2030", "Укажите дату начала отсчёта: /setstart ДД.ММ.ГГГГ")]
    [InlineData("/setstart 08.02.2030", "Дата должна быть не позже сегодняшней и не раньше чем 300 дней назад.")]
    [InlineData("/setstart 12.04.2029", "Дата должна быть не позже сегодняшней и не раньше чем 300 дней назад.")]
    public async Task Setstart_rejects_bad_input(string text, string expectedReply)
    {
        await HandleAsync(Msg(text));

        SingleReply().ShouldBe(expectedReply);
        _profiles.Profile.StageStartDate.ShouldBeNull();
    }

    [Fact]
    public async Task Settz_saves_known_zones_and_rejects_others()
    {
        const string usage = "Укажите часовой пояс: /settz Area/City, например /settz Europe/Berlin";

        await HandleAsync(Msg("/settz Europe/Berlin"));
        _profiles.Profile.TimeZone.ShouldBe("Europe/Berlin");
        SingleReply().ShouldBe("Часовой пояс: Europe/Berlin.");

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/settz Mars/Base"));
        await HandleAsync(Msg("/settz"));

        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { $"Неизвестный часовой пояс: Mars/Base. {usage}", usage });
        _profiles.Profile.TimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public async Task Setphone_saves_up_to_100_characters()
    {
        const string usage = "Укажите номер для экстренных случаев: /setphone <текст> (до 100 символов).";

        await HandleAsync(Msg("/setphone 112"));
        _profiles.Profile.EmergencyPhone.ShouldBe("112");
        SingleReply().ShouldBe("Телефон для экстренных случаев: 112");

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/setphone " + new string('x', 101)));
        await HandleAsync(Msg("/setphone"));

        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { usage, usage });
        _profiles.Profile.EmergencyPhone.ShouldBe("112");
    }

    [Fact]
    public async Task Setnote_saves_clears_and_limits()
    {
        const string usage = "Укажите заметку (до 500 символов): /setnote <текст>; /setnote - удаляет её.";

        await HandleAsync(Msg("/setnote test note"));
        _profiles.Profile.ContextNote.ShouldBe("test note");
        SingleReply().ShouldBe("Заметка сохранена.");

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/setnote -"));
        _profiles.Profile.ContextNote.ShouldBeNull();
        SingleReply().ShouldBe("Заметка удалена.");

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/setnote " + new string('x', 501)));
        await HandleAsync(Msg("/setnote"));

        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { usage, usage });
        _profiles.Profile.ContextNote.ShouldBeNull();
    }

    private const string ThresholdUsage =
        "Формат: /threshold <правило> <поле> <значение> — вводите значения, которые дал врач; " +
        "/threshold <правило> default — вернуть значения по умолчанию. Правила и поля: /thresholds.";

    private SafetyRuleInfo StoredRule(string key) => _profiles.Rules.Single(r => r.RuleKey == key);

    [Fact]
    public async Task Owner_sets_a_threshold()
    {
        await HandleAsync(Msg("/threshold glucose.any low_alert 4.0"));

        SingleReply().ShouldBe("Сохранено: glucose.any: low_urgent 3.0, low_alert 4.0, high_alert 11.0, high_urgent 13.9 — врач");
        var rule = StoredRule("glucose.any");
        rule.LowAlert.ShouldBe(4.0m);
        rule.Source.ShouldBe(SafetyRuleSources.Doctor);
        _profiles.LastUpdatedByUserId.ShouldBe(111);
    }

    [Fact]
    public async Task Owner_restores_the_default()
    {
        await HandleAsync(Msg("/threshold glucose.any low_alert 4.0"));
        _telegram.Sent.Clear();

        await HandleAsync(Msg("/threshold GLUCOSE.ANY default"));

        SingleReply().ShouldBe(
            "Восстановлены значения по умолчанию: glucose.any: low_urgent 3.0, low_alert 3.9, high_alert 11.0, high_urgent 13.9 — не подтверждено врачом");
        StoredRule("glucose.any").ShouldBe(SafetyRuleDefaults.Find("glucose.any")!);
    }

    [Fact]
    public async Task Unknown_rule_is_named()
    {
        await HandleAsync(Msg("/threshold glucose.unknown low_alert 4"));

        SingleReply().ShouldBe("Нет такого правила: glucose.unknown. Список: /thresholds.");
    }

    [Fact]
    public async Task Editor_errors_are_replied()
    {
        await HandleAsync(Msg("/threshold glucose.any low_alert abc"));

        SingleReply().ShouldBe("Значение: число больше 0 и меньше 1000, не больше двух знаков после запятой.");
        StoredRule("glucose.any").ShouldBe(SafetyRuleDefaults.Find("glucose.any")!);
    }

    [Theory]
    [InlineData("/threshold")]
    [InlineData("/threshold glucose.any")]
    [InlineData("/threshold glucose.any low_alert 4 5")]
    [InlineData("/threshold glucose.any reset")]
    public async Task Wrong_shape_gets_the_usage(string text)
    {
        await HandleAsync(Msg(text));

        SingleReply().ShouldBe(ThresholdUsage);
        StoredRule("glucose.any").ShouldBe(SafetyRuleDefaults.Find("glucose.any")!);
    }

    [Fact]
    public async Task Non_owner_cannot_change_thresholds()
    {
        await HandleAsync(Msg("/threshold glucose.any low_alert 4.0", userId: 222));

        SingleReply().ShouldBe(OwnerOnly);
        StoredRule("glucose.any").ShouldBe(SafetyRuleDefaults.Find("glucose.any")!);
        _profiles.LastUpdatedByUserId.ShouldBeNull();
    }

    // --- Extraction ---

    private const string WritingHand = "✍";
    private const string ThumbsUp = "\U0001F44D";

    private void ShouldHaveSentOnlyTheFailureNotice(IncomingMessage message)
    {
        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.ShouldBe((message.ChatId, message.TopicId, ExtractionReplies.FailureNotice, (int?)message.MessageId));
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Text_is_extracted_with_the_fast_tier_and_the_message_alone()
    {
        _profiles.Profile = _profiles.Profile with { TimeZone = "Europe/Berlin" };

        await HandleAsync(Msg("сахар 7.8 после обеда", "group", topicId: 7));

        var request = _gateway.Requests.ShouldHaveSingleItem();
        request.FamilyId.ShouldBe(42);
        request.BotId.ShouldBe(999);
        request.Tier.ShouldBe("fast");
        request.PreferredModel.ShouldBeNull();
        request.ChatId.ShouldBe(-100);
        request.TopicId.ShouldBe(7);
        request.TriggerMessageId.ShouldBe(1);
        var userTurn = request.Messages.ShouldHaveSingleItem();
        userTurn.Role.ShouldBe(LlmMessageRole.User);
        userTurn.Text.ShouldBe("сахар 7.8 после обеда");
        userTurn.Author.ShouldBeNull();
        request.SystemPrompt.ShouldStartWith("test extraction instructions");
        request.SystemPrompt.ShouldContain("- Time zone: Europe/Berlin");
        request.SystemPrompt.ShouldContain("- Current local date and time: 2030-02-07 11:00 (Thursday)");
    }

    [Fact]
    public async Task Recorded_events_get_the_writing_hand_reaction()
    {
        Answer(GlucoseAt930Json);
        var message = Msg("сахар 7.8 в 9:30", "group");

        await HandleAsync(message);

        var added = _events.Added.ShouldHaveSingleItem();
        added.FamilyId.ShouldBe(42);
        added.ProfileId.ShouldBe(1);
        added.Source.ShouldBe(new HealthEventSource(1, 999, -100, null, 111));
        var recorded = added.Events.ShouldHaveSingleItem();
        recorded.Type.ShouldBe("glucose");
        recorded.OccurredAt.ShouldBe(DateTimeOffset.Parse("2030-02-07T09:30:00Z"));
        recorded.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        recorded.OccurredAtSource.ShouldBe("stated");
        recorded.PayloadJson.ShouldBe("{\"value\":7.8,\"context\":\"after_meal_1h\"}");
        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reaction_falls_back_to_thumbs_up_once()
    {
        Answer(GlucoseAt930Json);
        _telegram.FailReactionTimes = 1;
        var message = Msg("сахар 7.8 в 9:30", "group");

        await HandleAsync(message);

        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)ThumbsUp) });
        _events.Added.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Failed_reactions_do_not_lose_the_recorded_events()
    {
        Answer(GlucoseAt930Json);
        _telegram.FailReactionTimes = 2;

        await HandleAsync(Msg("сахар 7.8 в 9:30", "group"));

        _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().Type.ShouldBe("glucose");
        _telegram.Reactions.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_events_means_no_reaction_and_no_reply()
    {
        // A group message that is not addressed to the bot (a private chat gets the hint instead).
        await HandleAsync(Msg("просто разговор", "group"));

        _gateway.Requests.Count.ShouldBe(1);
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ok")]
    [InlineData(" 7 ")]
    [InlineData("👍👍")]
    [InlineData("!!!")]
    public async Task Short_or_emoji_only_text_is_not_extracted(string text)
    {
        await HandleAsync(Msg(text));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Slash_text_for_other_bots_is_not_extracted()
    {
        await HandleAsync(Msg("/week@other_bot сахар 7.8"));

        _gateway.Requests.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unclear_item_gets_one_clarification_reply()
    {
        Answer("{\"events\":[],\"unclear\":[{\"fragment\":\"18\",\"reason\":\"unit\"}],\"is_question\":false}");
        var message = Msg("утром 18");

        await HandleAsync(message);

        // A Telegram reply even in a private chat.
        _telegram.Sent.ShouldHaveSingleItem()
            .ShouldBe((111L, (int?)null, "Не понял «18» — уточните единицы (нужно в ммоль/л).", (int?)message.MessageId));
        _telegram.Reactions.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fragment_not_in_the_message_gives_the_generic_clarification()
    {
        Answer("{\"events\":[],\"unclear\":[{\"fragment\":\"25\",\"reason\":\"unit\"}],\"is_question\":false}");

        await HandleAsync(Msg("утром 18"));

        SingleReply().ShouldBe("Не понял одно из значений — уточните, пожалуйста.");
    }

    [Fact]
    public async Task Invalid_events_are_not_recorded_valid_ones_are_and_one_clarification_is_sent()
    {
        Answer("{\"events\":[{\"type\":\"glucose\",\"value\":110,\"unit\":\"mg/dL\"},{\"type\":\"weight\",\"kg\":64.5}]," +
               "\"unclear\":[],\"is_question\":false}");
        var message = Msg("сахар 110 мг/дл и вес 64.5", "group");

        await HandleAsync(message);

        var recorded = _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem();
        recorded.Type.ShouldBe("weight");
        recorded.PayloadJson.ShouldBe("{\"kg\":64.5}");
        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)WritingHand) });
        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.Text.ShouldBe("Не понял «110» — уточните единицы (нужно в ммоль/л).");
        sent.ReplyToMessageId.ShouldBe(message.MessageId);
    }

    [Fact]
    public async Task Several_problems_still_give_one_reply()
    {
        Answer("{\"events\":[],\"unclear\":[{\"fragment\":\"18\",\"reason\":\"unit\"},{\"fragment\":\"25\",\"reason\":\"value\"}]," +
               "\"is_question\":false}");

        await HandleAsync(Msg("утром 18 и 25"));

        SingleReply().ShouldContain("«18»");
    }

    [Theory]
    [InlineData(LlmRefusalReason.NotConfigured, "сахар 7.8")]
    [InlineData(LlmRefusalReason.NotConfigured, "сильно болит голова")]
    [InlineData(LlmRefusalReason.RateLimited, "сахар 7.8")]
    [InlineData(LlmRefusalReason.RateLimited, "сильно болит голова")]
    [InlineData(LlmRefusalReason.DailyCapReached, "сахар 7.8")]
    [InlineData(LlmRefusalReason.DailyCapReached, "сильно болит голова")]
    [InlineData(LlmRefusalReason.AllModelsUnavailable, "сахар 7.8")]
    [InlineData(LlmRefusalReason.AllModelsUnavailable, "сильно болит голова")]
    [InlineData(LlmRefusalReason.BudgetExhausted, "сахар 7.8")]
    [InlineData(LlmRefusalReason.BudgetExhausted, "сильно болит голова")]
    [InlineData(LlmRefusalReason.Failed, "сахар 7.8")]
    [InlineData(LlmRefusalReason.Failed, "сильно болит голова")]
    public async Task Refusals_get_the_failure_notice(LlmRefusalReason reason, string text)
    {
        _gateway.NextResult = LlmResult.Refused(reason);
        var message = Msg(text, "group", topicId: 7);

        await HandleAsync(message);

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Theory]
    [InlineData("Конечно! Вот ответ")]
    [InlineData("{\"events\":[{\"type\":\"glucose\",\"value\":7.8}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"events\":\"glucose 7.8\",\"unclear\":[],\"is_question\":false}")]
    public async Task Unreadable_answer_gets_the_failure_notice_and_nothing_is_recorded(string answer)
    {
        Answer(answer);
        var message = Msg("сахар 7.8");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Fact]
    public async Task Gateway_exception_gets_the_failure_notice()
    {
        _gateway.ThrowOnComplete = new InvalidOperationException("simulated");
        var message = Msg("сахар 7.8", "group");

        await Should.NotThrowAsync(() => HandleAsync(message));

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Fact]
    public async Task Store_failure_gets_the_failure_notice_no_reaction_and_is_rethrown()
    {
        Answer(GlucoseAt930Json);
        _events.ThrowOnAdd = new InvalidOperationException("simulated");
        var message = Msg("сахар 7.8 в 9:30", "group");

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(message));

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Fact]
    public async Task Only_an_unknown_type_gets_one_clarification_and_nothing_is_stored()
    {
        Answer("{\"events\":[{\"type\":\"lab\",\"value\":4.2}],\"unclear\":[],\"is_question\":false}");
        var message = Msg("анализ 4.2");

        await HandleAsync(message);

        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.Text.ShouldBe(ExtractionReplies.GenericClarification);
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_extraction_prompt_gets_the_failure_notice_without_a_model_call()
    {
        _prompts.ExtractPrompt = null;
        var message = Msg("сахар 7.8");

        await HandleAsync(message);

        _gateway.Requests.ShouldBeEmpty();
        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Fact]
    public async Task Failure_notice_is_throttled_per_chat_and_topic()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var almostTenMinutes = new FixedClock(Now.AddMinutes(10).AddSeconds(-1));

        await HandleAsync(Msg("сахар 7.8", "group", topicId: 7));
        _telegram.Sent.Count.ShouldBe(1);

        await HandleAsync(Msg("сахар 7.9", "group", topicId: 7), clock: almostTenMinutes);
        _telegram.Sent.Count.ShouldBe(1);

        await HandleAsync(Msg("сахар 8.0", "group", topicId: 8), clock: almostTenMinutes);
        _telegram.Sent.Count.ShouldBe(2);

        await HandleAsync(Msg("сахар 8.1", "group", topicId: 7), clock: new FixedClock(Now.AddMinutes(10)));
        _telegram.Sent.Count.ShouldBe(3);
        _telegram.Sent.ShouldAllBe(s => s.Text == ExtractionReplies.FailureNotice);
        _telegram.Sent.Select(s => s.TopicId).ShouldBe(new int?[] { 7, 8, 7 });
    }

    // --- Safety rules and alerts ---

    private const string UrgentLow25 =
        "\U0001F6A8 Глюкоза: 2.5. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). " +
        "Порог 3.0 — не подтверждено врачом. Действуйте по плану врача.";

    private const string DoctorLow395 =
        "⚠️ Глюкоза: 3.95 — ниже порога 4.0 (порог от врача). Свяжитесь с врачом. " +
        "Если самочувствие ухудшается — вызовите скорую (103 или 112). Действуйте по плану врача.";

    private const string UrgentSystolic165 =
        "\U0001F6A8 Верхнее давление: 165. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). " +
        "Порог 160 — не подтверждено врачом.";

    private const string SystolicAlert150 =
        "⚠️ Верхнее давление: 150 — выше порога 140 (не подтверждено врачом). Свяжитесь с врачом. " +
        "Если самочувствие ухудшается — вызовите скорую (103 или 112).";

    private const string Combo15095 =
        "\U0001F6A8 Давление 150/95 вместе с симптомом «головная боль». Это может быть опасно. " +
        "Срочно свяжитесь с врачом или вызовите скорую (103 или 112). (не подтверждено врачом)";

    private const string GlucoseEventJson =
        "{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":2.5,\"unit\":\"mmol/L\",\"context\":\"other\"}";

    private const string Pressure15095EventJson = "{\"type\":\"blood_pressure\",\"day\":0,\"time\":null,\"systolic\":150,\"diastolic\":95}";

    private const string HeadacheEventJson = "{\"type\":\"symptom\",\"day\":0,\"time\":null,\"code\":\"headache\",\"text\":\"болит голова\"}";

    private static string GlucoseAnswer(string value, string context = "other", string time = "null", int day = 0) =>
        $"{{\"events\":[{{\"type\":\"glucose\",\"day\":{day},\"time\":{time},\"value\":{value},\"unit\":\"mmol/L\",\"context\":\"{context}\"}}]," +
        "\"unclear\":[],\"is_question\":false}";

    private static string EventsAnswer(params string[] events) =>
        $"{{\"events\":[{string.Join(",", events)}],\"unclear\":[],\"is_question\":false}}";

    private void ReplaceRule(string key, SafetyRuleInfo? rule)
    {
        var index = _profiles.Rules.FindIndex(r => r.RuleKey == key);
        if (rule is null)
        {
            _profiles.Rules.RemoveAt(index);
        }
        else
        {
            _profiles.Rules[index] = rule;
        }
    }

    private IReadOnlyList<string>? SingleAddedFlags() =>
        _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().Flags;

    [Fact]
    public async Task Dangerous_reading_is_recorded_marked_and_alerted()
    {
        Answer(GlucoseAnswer("2.5"));
        var message = Msg("сахар 2.5", "group", topicId: 7);
        // The claim must happen after the reaction and before the alert is sent.
        (int Reactions, int Sent)? atClaim = null;
        _alerts.OnClaim = () => atClaim = (_telegram.Reactions.Count, _telegram.Sent.Count);

        await HandleAsync(message);

        SingleAddedFlags().ShouldNotBeNull().ShouldBeEmpty();
        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, UrgentLow25, (int?)message.MessageId) });
        var claim = _alerts.Claims.ShouldHaveSingleItem();
        claim.FamilyId.ShouldBe(42);
        claim.Alert.ShouldBe(new NewSafetyAlert(1, "glucose.any", "urgent", 3.0m, "guideline_default", -100, 7));
        atClaim.ShouldBe((1, 0));
    }

    [Fact]
    public async Task Out_of_target_is_only_a_flag()
    {
        Answer(GlucoseAt930Json);

        await HandleAsync(Msg("сахар 7.8 в 9:30"));

        SingleAddedFlags().ShouldBe(new[] { "out_of_target" });
        _telegram.Sent.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task Doctor_threshold_gives_the_doctor_label()
    {
        ReplaceRule("glucose.any", SafetyRuleDefaults.Find("glucose.any")! with { LowAlert = 4.0m, Source = "doctor" });
        Answer(GlucoseAnswer("3.95"));

        await HandleAsync(Msg("сахар 3.95"));

        SingleReply().ShouldBe(DoctorLow395);
        var claim = _alerts.Claims.ShouldHaveSingleItem().Alert;
        claim.Threshold.ShouldBe(4.0m);
        claim.ThresholdSource.ShouldBe("doctor");
        claim.Level.ShouldBe("alert");
    }

    [Fact]
    public async Task A_rule_the_family_does_not_have_never_fires()
    {
        ReplaceRule("glucose.any", null);
        Answer(GlucoseAnswer("2.5"));
        var message = Msg("сахар 2.5");

        await HandleAsync(message);

        _events.Added.ShouldHaveSingleItem();
        _telegram.Reactions.ShouldBe(new[] { (111L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_already_claimed_alert_is_not_sent_again()
    {
        _alerts.ClaimResult = false;
        Answer(GlucoseAnswer("2.5"));

        await HandleAsync(Msg("сахар 2.5"));

        _alerts.Claims.ShouldHaveSingleItem();
        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldHaveSingleItem().Emoji.ShouldBe(WritingHand);
    }

    [Fact]
    public async Task A_failed_claim_still_sends_the_alert()
    {
        _alerts.ThrowOnClaim = true;
        Answer(GlucoseAnswer("2.5"));

        await Should.NotThrowAsync(() => HandleAsync(Msg("сахар 2.5")));

        SingleReply().ShouldBe(UrgentLow25);
        _alerts.Claims.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_alert_comes_before_the_clarification()
    {
        Answer($"{{\"events\":[{GlucoseEventJson}],\"unclear\":[{{\"fragment\":\"18\",\"reason\":\"unit\"}}],\"is_question\":false}}");

        await HandleAsync(Msg("сахар 2.5 и 18"));

        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { UrgentLow25, "Не понял «18» — уточните единицы (нужно в ммоль/л)." });
    }

    [Fact]
    public async Task Each_dangerous_reading_gets_its_own_alert()
    {
        Answer(EventsAnswer(GlucoseEventJson, "{\"type\":\"blood_pressure\",\"day\":0,\"time\":null,\"systolic\":165,\"diastolic\":100}"));

        await HandleAsync(Msg("сахар 2.5, давление 165/100"));

        _telegram.Sent.Select(s => s.Text).ShouldBe(new[] { UrgentLow25, UrgentSystolic165 });
        _alerts.Claims.Select(c => (c.Alert.EventId, c.Alert.RuleKey))
            .ShouldBe(new[] { (1L, "glucose.any"), (2L, "blood_pressure.systolic") });
    }

    [Fact]
    public async Task An_old_reading_is_flagged_and_not_alerted()
    {
        // The day before at 09:00 UTC: 25 hours old.
        Answer(GlucoseAnswer("2.5", time: "\"09:00\"", day: -1));
        var message = Msg("вчера в 9 сахар 2.5");

        await HandleAsync(message);

        SingleAddedFlags().ShouldBe(new[] { "old_value_not_alerted" });
        _telegram.Sent.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
        _telegram.Reactions.ShouldBe(new[] { (111L, message.MessageId, (string?)WritingHand) });
    }

    [Fact]
    public async Task The_combination_uses_earlier_readings_from_the_store()
    {
        _events.ActiveEvents.Add(new HealthEventInfo(50, "blood_pressure", Now.AddHours(-3), "{\"systolic\":150,\"diastolic\":95,\"pulse\":null}", 7));
        Answer(EventsAnswer(HeadacheEventJson));

        var message = Msg("болит голова");

        await HandleAsync(message);

        // A Telegram reply even in a private chat.
        _telegram.Sent.ShouldHaveSingleItem().ShouldBe((111L, (int?)null, Combo15095, (int?)message.MessageId));
        _alerts.Claims.ShouldHaveSingleItem().Alert
            .ShouldBe(new NewSafetyAlert(1, "combo.bp_symptoms", "urgent", null, "guideline_default", 111, null));
        _events.LastRange.ShouldBe((42L, 1L, Now.AddHours(-24), Now.AddHours(24).AddSeconds(1)));
    }

    [Fact]
    public async Task Pressure_and_symptom_in_one_message_alert_once()
    {
        Answer(EventsAnswer(Pressure15095EventJson, HeadacheEventJson));

        await HandleAsync(Msg("давление 150/95, болит голова"));

        SingleReply().ShouldBe(Combo15095);
        var claim = _alerts.Claims.ShouldHaveSingleItem().Alert;
        claim.EventId.ShouldBe(1);
        claim.RuleKey.ShouldBe("combo.bp_symptoms");
    }

    [Fact]
    public async Task Without_the_combination_rule_no_earlier_readings_are_read()
    {
        ReplaceRule("combo.bp_symptoms", null);
        Answer(EventsAnswer(Pressure15095EventJson));

        await HandleAsync(Msg("давление 150/95"));

        SingleReply().ShouldBe(SystolicAlert150);
        _events.LastRange.ShouldBeNull();
    }

    [Fact]
    public async Task Insulin_entries_are_never_checked()
    {
        Answer("{\"events\":[{\"type\":\"insulin\",\"day\":0,\"time\":null,\"kind\":\"short\",\"units\":40}],\"unclear\":[],\"is_question\":false}");

        await HandleAsync(Msg("инсулин 40"));

        SingleAddedFlags().ShouldNotBeNull().ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_alert_send_does_not_fail_the_update()
    {
        _telegram.ThrowOnSend = true;
        Answer(GlucoseAnswer("2.5"));

        await Should.NotThrowAsync(() => HandleAsync(Msg("сахар 2.5")));

        _events.Added.ShouldHaveSingleItem();
        _alerts.Claims.ShouldHaveSingleItem();
        _telegram.Reactions.ShouldHaveSingleItem().Emoji.ShouldBe(WritingHand);
        // Tried twice, then an Error with the rule key and the message id only.
        _log.Entries.Count(e => e.Message.StartsWith("failed to send health reply", StringComparison.Ordinal)).ShouldBe(2);
        _log.Entries.ShouldContain((LogLevel.Error, "Safety alert glucose.any for message 1 could not be sent"));
    }

    [Fact]
    public async Task A_failed_alert_send_is_retried_once()
    {
        _telegram.ThrowOnSendNumber = 1;
        Answer(GlucoseAnswer("2.5"));
        var message = Msg("сахар 2.5");

        await HandleAsync(message);

        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, UrgentLow25, (int?)message.MessageId) });
        _alerts.Claims.ShouldHaveSingleItem();
        _log.Entries.ShouldNotContain(e => e.Message.Contains("could not be sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_failure_with_a_dangerous_value_sends_the_alert_as_not_recorded()
    {
        _events.ThrowOnAdd = new InvalidOperationException("simulated");
        Answer(GlucoseAnswer("2.5"));
        var message = Msg("сахар 2.5", "group");

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(message));

        _telegram.Sent.ShouldBe(new[]
        {
            (-100L, (int?)null, UrgentLow25 + "\nНичего не записано — повторите сообщение позже.", (int?)message.MessageId)
        });
        _alerts.Claims.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rules_failure_with_a_reading_in_the_text_always_gets_the_notice()
    {
        _profiles.ThrowOnGetRules = true;
        Answer(GlucoseAnswer("5.0"));
        var first = Msg("сахар 5.0", "group", topicId: 7);
        var second = Msg("сахар 5.0", "group", topicId: 7);

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(first));
        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(second));

        _telegram.Sent.ShouldBe(new[]
        {
            (-100L, (int?)7, ExtractionReplies.FailureNotice, (int?)first.MessageId),
            (-100L, (int?)7, ExtractionReplies.FailureNotice, (int?)second.MessageId)
        });
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rules_failure_without_a_reading_in_the_text_keeps_the_throttled_notice()
    {
        _profiles.ThrowOnGetRules = true;
        Answer(EventsAnswer("{\"type\":\"weight\",\"day\":0,\"time\":null,\"kg\":64.5}"));

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(Msg("вес 64.5", "group", topicId: 7)));
        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(Msg("вес 64.5", "group", topicId: 7)));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(ExtractionReplies.FailureNotice);
    }

    // --- Quick scan when extraction fails ---

    private const string NotRecorded = "\nНичего не записано — повторите сообщение позже.";

    private void ShouldHaveSentOnlyTheQuickScanAlert(IncomingMessage message, string alert)
    {
        _telegram.Sent.ShouldHaveSingleItem().ShouldBe((message.ChatId, message.TopicId, alert + NotRecorded, (int?)message.MessageId));
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(LlmRefusalReason.NotConfigured)]
    [InlineData(LlmRefusalReason.RateLimited)]
    [InlineData(LlmRefusalReason.DailyCapReached)]
    [InlineData(LlmRefusalReason.AllModelsUnavailable)]
    [InlineData(LlmRefusalReason.Failed)]
    [InlineData(LlmRefusalReason.BudgetExhausted)]
    public async Task Quick_scan_alerts_when_the_model_refuses(LlmRefusalReason reason)
    {
        _gateway.NextResult = LlmResult.Refused(reason);
        var message = Msg("сахар 2.5", "group", topicId: 7);

        await HandleAsync(message);

        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, UrgentLow25 + NotRecorded, (int?)message.MessageId) });
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quick_scan_runs_when_the_answer_is_unreadable()
    {
        Answer("no json");
        var message = Msg("сахар 2.5");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentLow25);
    }

    [Fact]
    public async Task Quick_scan_runs_when_the_gateway_throws()
    {
        _gateway.ThrowOnComplete = new InvalidOperationException("simulated");
        var message = Msg("сахар 2.5", "group");

        await Should.NotThrowAsync(() => HandleAsync(message));

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentLow25);
    }

    [Fact]
    public async Task Quick_scan_runs_when_the_extraction_prompt_is_missing()
    {
        _prompts.ExtractPrompt = null;
        var message = Msg("сахар 2.5");

        await HandleAsync(message);

        _gateway.Requests.ShouldBeEmpty();
        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentLow25);
    }

    [Fact]
    public async Task Quick_scan_finds_blood_pressure()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var message = Msg("давление 165 на 100");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentSystolic165);
    }

    [Fact]
    public async Task Quick_scan_sends_only_the_most_severe_hit()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var message = Msg("давление 150/95, сахар 2.5");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentLow25);
    }

    [Fact]
    public async Task Quick_scan_uses_the_doctors_values()
    {
        ReplaceRule("glucose.any", SafetyRuleDefaults.Find("glucose.any")! with { LowAlert = 4.0m, Source = "doctor" });
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var message = Msg("сахар 3.9");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message,
            "⚠️ Глюкоза: 3.9 — ниже порога 4.0 (порог от врача). Свяжитесь с врачом. " +
            "Если самочувствие ухудшается — вызовите скорую (103 или 112). Действуйте по плану врача.");
    }

    [Theory]
    [InlineData("сахар 7.8")]
    [InlineData("у неё 2.5 утром")]
    [InlineData("сильно болит голова")]
    public async Task Without_a_dangerous_hit_the_failure_notice_is_sent(string text)
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var message = Msg(text);

        await HandleAsync(message);

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    [Theory]
    [InlineData("сахар 45", "Не понял «45» — уточните единицы (нужно в ммоль/л).")]
    [InlineData("сахар 250", "Не понял «250» — уточните единицы (нужно в ммоль/л).")]
    [InlineData("сахар 0.3", "Не понял «0.3» — уточните значение.")]
    [InlineData("давление 50/40", "Не понял «50/40» — уточните значение.")]
    public async Task An_implausible_quick_scan_hit_gets_the_clarification_without_throttling(string text, string clarification)
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var first = Msg(text, "group", topicId: 7);
        var second = Msg(text, "group", topicId: 7);

        await HandleAsync(first);
        await HandleAsync(second);

        _telegram.Sent.ShouldBe(new[]
        {
            (-100L, (int?)7, clarification, (int?)first.MessageId),
            (-100L, (int?)7, clarification, (int?)second.MessageId)
        });
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quick_scan_alerts_are_not_throttled_and_keep_the_notice_slot_free()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);

        await HandleAsync(Msg("сахар 2.5", "group", topicId: 7));
        await HandleAsync(Msg("сахар 2.4", "group", topicId: 7));
        await HandleAsync(Msg("сахар 7.8", "group", topicId: 7));
        await HandleAsync(Msg("сахар 7.9", "group", topicId: 7));

        _telegram.Sent.Count.ShouldBe(3);
        _telegram.Sent[0].Text.ShouldBe(UrgentLow25 + NotRecorded);
        _telegram.Sent[1].Text.ShouldStartWith("\U0001F6A8 Глюкоза: 2.4.");
        _telegram.Sent[1].Text.ShouldEndWith(NotRecorded);
        _telegram.Sent[2].Text.ShouldBe(ExtractionReplies.FailureNotice);
        _events.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quick_scan_falls_back_to_the_notice_when_the_rules_cannot_be_read()
    {
        _profiles.ThrowOnGetRules = true;
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var message = Msg("сахар 2.5");

        await Should.NotThrowAsync(() => HandleAsync(message));

        ShouldHaveSentOnlyTheFailureNotice(message);
    }

    // --- Quick scan when the model finds no readings ---

    [Fact]
    public async Task Quick_scan_alerts_when_the_model_finds_no_readings_in_a_dangerous_glucose_message()
    {
        Answer(NoEventsJson);
        var message = Msg("сахар 2.5", "group", topicId: 7);

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentLow25);
    }

    [Fact]
    public async Task Quick_scan_alerts_when_the_model_finds_no_readings_in_a_dangerous_pressure_message()
    {
        Answer(NoEventsJson);
        var message = Msg("давление 165 на 100");

        await HandleAsync(message);

        ShouldHaveSentOnlyTheQuickScanAlert(message, UrgentSystolic165);
    }

    [Theory]
    [InlineData("сахар 3.9")]
    [InlineData("сахар 10.9")]
    [InlineData("давление 139/89")]
    [InlineData("сахар 7.8")]
    [InlineData("как прошёл день?")]
    public async Task A_message_without_readings_or_a_dangerous_value_stays_silent(string text)
    {
        Answer(NoEventsJson);

        await HandleAsync(Msg(text, "group"));

        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_readings_without_a_dangerous_value_never_uses_the_failure_notice_slot()
    {
        Answer(NoEventsJson);
        await HandleAsync(Msg("сахар 7.8", "group", topicId: 7));

        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.Failed);
        var failed = Msg("сахар 7.9", "group", topicId: 7);
        await HandleAsync(failed);

        ShouldHaveSentOnlyTheFailureNotice(failed);
    }

    [Fact]
    public async Task An_implausible_value_the_model_missed_gets_the_clarification()
    {
        Answer(NoEventsJson);
        var message = Msg("сахар 45");

        await HandleAsync(message);

        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, "Не понял «45» — уточните единицы (нужно в ммоль/л).", (int?)message.MessageId) });
        _events.Added.ShouldBeEmpty();
    }

    // --- Quick scan when the model records some readings but misses one ---

    private const string Weight645EventJson = "{\"type\":\"weight\",\"day\":0,\"time\":null,\"kg\":64.5}";

    [Fact]
    public async Task A_dangerous_glucose_the_model_missed_is_alerted_next_to_the_recorded_events()
    {
        Answer(EventsAnswer(Weight645EventJson));
        var message = Msg("вес 64.5, сахар 2.5", "group", topicId: 7);

        await HandleAsync(message);

        _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().Type.ShouldBe("weight");
        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, UrgentLow25 + NotRecorded, (int?)message.MessageId) });
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dangerous_pressure_the_model_missed_is_alerted_next_to_a_recorded_glucose()
    {
        Answer(GlucoseAnswer("5.0"));

        await HandleAsync(Msg("сахар 5.0, давление 165/100"));

        SingleReply().ShouldBe(UrgentSystolic165 + NotRecorded);
        _events.Added.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_metric_the_model_extracted_is_not_scanned_again()
    {
        // The model read the glucose differently (and safely); the scan must not second-guess it.
        Answer(EventsAnswer(Weight645EventJson, "{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":5.2,\"unit\":\"mmol/L\",\"context\":\"other\"}"));

        await HandleAsync(Msg("вес 64.5, сахар 2.5 вчера было, сегодня 5.2"));

        _telegram.Sent.ShouldBeEmpty();
        _events.Added.ShouldHaveSingleItem().Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Quick_scan_does_not_run_when_the_model_returned_an_unclear_item()
    {
        Answer("{\"events\":[],\"unclear\":[{\"fragment\":\"2.5\",\"reason\":\"unit\"}],\"is_question\":false}");

        await HandleAsync(Msg("сахар 2.5"));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldNotContain(NotRecorded);
        _events.Added.ShouldBeEmpty();
    }

    // --- Edits ---

    private const string UnitClarification18 = "Не понял «18» — уточните единицы (нужно в ммоль/л).";

    // An earlier glucose record of the edited message (message db id 1).
    private static readonly HealthEventInfo OldGlucose78 = new(5, "glucose", Now, "{\"value\":7.8,\"context\":\"other\"}", 1);

    private IncomingMessage Edit(
        string? text, string chatType = "private", int? topicId = null, DateTimeOffset? sentAt = null, MessageKind kind = MessageKind.Text) =>
        new(ChatId: chatType == "private" ? 111 : -100, ChatType: chatType, ChatTitle: chatType == "private" ? null : "test group",
            TopicId: topicId, MessageId: _nextMessageId++, UserId: 111, Username: "test_user",
            Text: text, Kind: kind, IsEdit: true, SentAt: sentAt ?? Now, EditedAt: Now,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    [Fact]
    public async Task An_edit_replaces_the_records_and_alerts_on_the_changed_value()
    {
        _events.ReplaceDeletes.Add(OldGlucose78);
        Answer(GlucoseAnswer("2.5"));
        var edit = Edit("сахар 2.5", "group", topicId: 7);

        await HandleAsync(edit, StoreOutcome.Updated);

        _events.Added.ShouldBeEmpty();
        var replaced = _events.Replaced.ShouldHaveSingleItem();
        replaced.FamilyId.ShouldBe(42);
        replaced.ProfileId.ShouldBe(1);
        replaced.Source.ShouldBe(new HealthEventSource(1, 999, -100, 7, 111));
        var reading = replaced.Events.ShouldHaveSingleItem();
        reading.Type.ShouldBe("glucose");
        reading.PayloadJson.ShouldBe("{\"value\":2.5,\"context\":\"other\"}");
        reading.OccurredAt.ShouldBe(Now);
        reading.Flags.ShouldNotBeNull().ShouldBeEmpty();
        var request = _gateway.Requests.ShouldHaveSingleItem();
        request.TriggerMessageId.ShouldBe(1);
        request.Messages.ShouldHaveSingleItem().Text.ShouldBe("сахар 2.5");
        // The message already carries its reaction.
        _telegram.Reactions.ShouldBeEmpty();
        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, UrgentLow25, (int?)edit.MessageId) });
        _alerts.Claims.ShouldHaveSingleItem().Alert
            .ShouldBe(new NewSafetyAlert(1, "glucose.any", "urgent", 3.0m, "guideline_default", -100, 7));
    }

    [Fact]
    public async Task An_unchanged_dangerous_reading_is_not_alerted_again()
    {
        _events.ReplaceKeeps[0] = 5;
        _alerts.ClaimResult = false;
        Answer(GlucoseAnswer("2.5"));

        await HandleAsync(Edit("Сахар 2.5"), StoreOutcome.Updated);

        var claim = _alerts.Claims.ShouldHaveSingleItem().Alert;
        claim.EventId.ShouldBe(5);
        claim.RuleKey.ShouldBe("glucose.any");
        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_kept_reading_that_never_alerted_alerts_now()
    {
        // No alert row for event 5 yet (e.g. the threshold changed since): the claim is won.
        _events.ReplaceKeeps[0] = 5;
        Answer(GlucoseAnswer("2.5"));

        await HandleAsync(Edit("сахар 2.5"), StoreOutcome.Updated);

        _alerts.Claims.ShouldHaveSingleItem().Alert.EventId.ShouldBe(5);
        SingleReply().ShouldBe(UrgentLow25);
    }

    [Fact]
    public async Task An_edit_of_a_message_without_records_sets_the_reaction()
    {
        Answer(GlucoseAt930Json);
        var edit = Edit("сахар 7.8 в 9:30", "group");

        await HandleAsync(edit, StoreOutcome.Updated);

        _telegram.Reactions.ShouldBe(new[] { (-100L, edit.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBeEmpty();
        _events.Replaced.ShouldHaveSingleItem().Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_edit_without_readings_deletes_the_records_and_clears_the_reaction()
    {
        _events.ReplaceDeletes.Add(OldGlucose78);
        Answer(NoEventsJson);
        var edit = Edit("просто разговор");

        await HandleAsync(edit, StoreOutcome.Updated);

        _events.Replaced.ShouldHaveSingleItem().Events.ShouldBeEmpty();
        _telegram.Reactions.ShouldBe(new[] { (111L, edit.MessageId, (string?)null) });
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_into_short_text_deletes_the_records_without_a_model_call()
    {
        _events.ReplaceDeletes.Add(OldGlucose78);
        var edit = Edit("ок");

        await HandleAsync(edit, StoreOutcome.Updated);

        _gateway.Requests.ShouldBeEmpty();
        _events.Replaced.ShouldHaveSingleItem().Events.ShouldBeEmpty();
        _telegram.Reactions.ShouldBe(new[] { (111L, edit.MessageId, (string?)null) });
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_with_an_unclear_value_gets_the_clarification()
    {
        _events.ReplaceDeletes.Add(OldGlucose78);
        Answer("{\"events\":[],\"unclear\":[{\"fragment\":\"18\",\"reason\":\"unit\"}],\"is_question\":false}");
        var edit = Edit("утром 18");

        await HandleAsync(edit, StoreOutcome.Updated);

        _events.Replaced.ShouldHaveSingleItem().Events.ShouldBeEmpty();
        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, UnitClarification18, (int?)edit.MessageId) });
        _telegram.Reactions.ShouldBe(new[] { (111L, edit.MessageId, (string?)null) });
    }

    [Theory]
    [InlineData("давление 120/80", false)]
    [InlineData("сахар 2.5", true)]
    public async Task A_failed_edit_keeps_the_records(string text, bool alert)
    {
        _events.ReplaceDeletes.Add(OldGlucose78);
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.RateLimited);
        var edit = Edit(text);

        await HandleAsync(edit, StoreOutcome.Updated);

        _events.Replaced.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.ReplyToMessageId.ShouldBe(edit.MessageId);
        sent.Text.ShouldBe(alert ? UrgentLow25 + NotRecorded : ExtractionReplies.FailureNotice);
    }

    public enum FailureKind { Refusal, Exception, Unreadable, MissingPrompt }

    private void MakeExtractionFail(FailureKind kind)
    {
        switch (kind)
        {
            case FailureKind.Refusal:
                _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.RateLimited);
                break;
            case FailureKind.Exception:
                _gateway.ThrowOnComplete = new InvalidOperationException("simulated");
                break;
            case FailureKind.Unreadable:
                Answer("not json at all");
                break;
            default:
                _prompts.ExtractPrompt = null;
                break;
        }
    }

    [Theory]
    [InlineData(FailureKind.Refusal)]
    [InlineData(FailureKind.Exception)]
    [InlineData(FailureKind.Unreadable)]
    [InlineData(FailureKind.MissingPrompt)]
    public async Task A_failed_edit_gets_the_notice_even_when_the_throttle_slot_was_just_used(FailureKind kind)
    {
        MakeExtractionFail(kind);
        _throttle.TryAcquire(Bot.TelegramBotId, 111, null, Now).ShouldBeTrue();
        var edit = Edit("вес 71.5");

        await HandleAsync(edit, StoreOutcome.Updated);

        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, ExtractionReplies.FailureNotice, (int?)edit.MessageId) });
        // The edit did not use up the slot: it is still taken from the earlier notice, not renewed.
        _throttle.TryAcquire(Bot.TelegramBotId, 111, null, Now.AddMinutes(10).AddSeconds(-1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(FailureKind.Refusal)]
    [InlineData(FailureKind.Exception)]
    [InlineData(FailureKind.Unreadable)]
    [InlineData(FailureKind.MissingPrompt)]
    public async Task A_failed_first_time_message_stays_throttled_when_the_slot_was_just_used(FailureKind kind)
    {
        MakeExtractionFail(kind);
        _throttle.TryAcquire(Bot.TelegramBotId, 111, null, Now).ShouldBeTrue();

        await HandleAsync(Msg("вес 71.5"));

        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_store_failure_on_an_edit_gets_the_failure_notice_and_is_rethrown()
    {
        _events.ThrowOnReplace = new InvalidOperationException("simulated");
        Answer(GlucoseAt930Json);
        var edit = Edit("сахар 7.8 в 9:30", "group");

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(edit, StoreOutcome.Updated));

        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)null, ExtractionReplies.FailureNotice, (int?)edit.MessageId) });
        _telegram.Reactions.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edits_of_messages_sent_more_than_24_hours_ago_are_ignored()
    {
        Answer(GlucoseAnswer("2.5"));

        await HandleAsync(Edit("сахар 2.5", sentAt: Now.AddHours(-24).AddSeconds(-1)), StoreOutcome.Updated);

        _gateway.Requests.ShouldBeEmpty();
        _events.Replaced.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(0);

        // Exactly 24 hours is still read; the reading takes the send time, too old to alert.
        await HandleAsync(Edit("сахар 2.5", sentAt: Now.AddHours(-24)), StoreOutcome.Updated);

        _gateway.Requests.Count.ShouldBe(1);
        _events.Replaced.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().Flags.ShouldBe(new[] { "old_value_not_alerted" });
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Command_and_non_text_edits_are_ignored()
    {
        await HandleAsync(Edit("/today"), StoreOutcome.Updated);
        await HandleAsync(Edit(null, kind: MessageKind.Photo), StoreOutcome.Updated);

        _telegram.Sent.ShouldBeEmpty();
        _gateway.Requests.ShouldBeEmpty();
        _events.Replaced.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_edit_of_a_message_the_bot_never_stored_is_read_as_an_edit()
    {
        Answer(GlucoseAt930Json);
        var edit = Edit("сахар 7.8 в 9:30");

        await HandleAsync(edit, StoreOutcome.Stored);

        _events.Added.ShouldBeEmpty();
        _events.Replaced.ShouldHaveSingleItem();
        _telegram.Reactions.ShouldBe(new[] { (111L, edit.MessageId, (string?)WritingHand) });
    }

    [Theory]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Redelivered_edits_are_ignored(StoreOutcome outcome)
    {
        Answer(GlucoseAnswer("2.5"));

        await HandleAsync(Edit("сахар 2.5"), outcome);

        _gateway.Requests.ShouldBeEmpty();
        _events.Replaced.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_never_pairs_with_the_messages_own_earlier_readings()
    {
        // The earlier pressure reading came from the edited message itself (message db id 1).
        _events.ActiveEvents.Add(new HealthEventInfo(50, "blood_pressure", Now.AddHours(-1), "{\"systolic\":150,\"diastolic\":95,\"pulse\":null}", 1));
        Answer(EventsAnswer(HeadacheEventJson));

        await HandleAsync(Edit("болит голова"), StoreOutcome.Updated);

        _telegram.Sent.ShouldBeEmpty();
        _alerts.Claims.ShouldBeEmpty();
    }

    // --- Answers to addressed questions ---

    private const string QuestionJson = "{\"events\":[],\"unclear\":[],\"is_question\":true}";
    private const string Footer = "\n\nНе заменяю врача.";
    private const string DoseRefusal = "Я не даю советов по дозам лекарств. Это вопрос к врачу — запишите его, чтобы спросить на приёме.";
    private const string FailedText = "Не получилось ответить, попробуйте ещё раз.";

    private void AskAndAnswer(string answer)
    {
        _gateway.Results.Enqueue(LlmResult.Answered(QuestionJson, "haiku"));
        _gateway.Results.Enqueue(LlmResult.Answered(answer, "sonnet"));
    }

    private void AskOnly() => _gateway.Results.Enqueue(LlmResult.Answered(QuestionJson, "haiku"));

    [Fact]
    public async Task Private_question_gets_a_smart_answer_with_the_footer()
    {
        AskAndAnswer("Тестовый ответ.");

        await HandleAsync(Msg("какой сахар считается нормой натощак?"));

        _gateway.Requests.Count.ShouldBe(2);
        _gateway.Requests[0].Tier.ShouldBe("fast");
        var answer = _gateway.Requests[1];
        answer.Tier.ShouldBe("smart");
        answer.PreferredModel.ShouldBeNull();
        answer.FamilyId.ShouldBe(42);
        answer.BotId.ShouldBe(999);
        answer.ChatId.ShouldBe(111);
        answer.TopicId.ShouldBeNull();
        answer.TriggerMessageId.ShouldBe(1);
        var last = answer.Messages[^1];
        last.Role.ShouldBe(LlmMessageRole.User);
        last.Text.ShouldBe("какой сахар считается нормой натощак?");
        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, "Тестовый ответ." + Footer, (int?)null) });
        var stored = _messages.OutgoingMessages.ShouldHaveSingleItem();
        stored.BotId.ShouldBe(999);
        stored.ChatId.ShouldBe(111);
        stored.TopicId.ShouldBeNull();
        stored.ChatType.ShouldBe("private");
        stored.Text.ShouldBe("Тестовый ответ." + Footer);
        _telegram.Reactions.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
        _telegram.ChatActionsSent.ShouldContain((111L, (int?)null, "typing"));
    }

    [Fact]
    public async Task The_answer_prompt_carries_the_profile_context()
    {
        _profiles.Profile = _profiles.Profile with { StageStartDate = new DateOnly(2030, 1, 15), ContextNote = "test context note" };
        _events.ActiveEvents.Add(new HealthEventInfo(5, "glucose", Now.AddMinutes(-30), "{\"value\":7.8,\"context\":\"after_meal_1h\"}", 3));
        AskAndAnswer("Тестовый ответ.");

        await HandleAsync(Msg("какой сахар считается нормой натощак?"));

        var system = _gateway.Requests[1].SystemPrompt;
        system.ShouldStartWith("test answer instructions");
        system.ShouldContain("- Stage week: 3 нед. 2 дн.");
        system.ShouldContain("test context note");
        system.ShouldContain("glucose.any: low_urgent 3.0, low_alert 3.9, high_alert 11.0, high_urgent 13.9 — не подтверждено врачом");
        system.ShouldContain("07.02 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)");
        _gateway.Requests[0].SystemPrompt.ShouldNotContain("test context note");
        _events.LastRange.ShouldBe((42L, 1L, Now.AddHours(-24), Now.AddHours(1)));
    }

    [Fact]
    public async Task Earlier_messages_go_before_the_question()
    {
        await _messages.StoreAsync(999, 1, Msg("раньше был вопрос"), CancellationToken.None);
        await _messages.StoreOutgoingAsync(999, 111, null, "private", 500, "раньше был ответ", CancellationToken.None);
        AskAndAnswer("Тестовый ответ.");

        await HandleAsync(Msg("а сейчас?"), messageDbId: 10);

        _gateway.Requests[1].Messages.Select(m => (m.Role, m.Text)).ShouldBe(new[]
        {
            (LlmMessageRole.User, "раньше был вопрос"),
            (LlmMessageRole.Assistant, "раньше был ответ"),
            (LlmMessageRole.User, "а сейчас?")
        });
    }

    [Fact]
    public async Task History_is_capped_at_ten_messages()
    {
        for (var i = 1; i <= 12; i++)
        {
            await _messages.StoreAsync(999, i, Msg($"сообщение {i}"), CancellationToken.None);
        }

        AskAndAnswer("Тестовый ответ.");

        await HandleAsync(Msg("а сейчас?"), messageDbId: 100);

        var messages = _gateway.Requests[1].Messages;
        messages.Count.ShouldBe(11);
        messages[0].Text.ShouldBe("сообщение 3");
        messages[^1].Text.ShouldBe("а сейчас?");
    }

    [Fact]
    public async Task Group_question_without_mention_or_reply_is_not_answered()
    {
        AskOnly();

        await HandleAsync(Msg("какой сахар считается нормой?", "group", topicId: 7));

        _gateway.Requests.Count.ShouldBe(1);
        _telegram.Sent.ShouldBeEmpty();
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("@test_health_bot какой сахар считается нормой?")]
    [InlineData("какой сахар считается нормой, @TEST_HEALTH_BOT?")]
    public async Task Group_question_with_a_mention_is_answered_in_the_topic(string text)
    {
        AskAndAnswer("Тестовый ответ.");
        var message = Msg(text, "group", topicId: 7);

        await HandleAsync(message);

        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, "Тестовый ответ." + Footer, (int?)message.MessageId) });
        _messages.OutgoingMessages.ShouldHaveSingleItem().TopicId.ShouldBe(7);
    }

    [Fact]
    public async Task A_genuine_reply_to_the_bot_is_answered()
    {
        AskAndAnswer("Тестовый ответ.");

        await HandleAsync(Msg("а это нормально?", "group", topicId: 7, replyToMessageId: 50, replyToUserId: 999));

        _gateway.Requests.Count.ShouldBe(2);
        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Тестовый ответ." + Footer);
    }

    [Fact]
    public async Task A_reply_to_the_topic_root_is_not_answered()
    {
        AskOnly();

        await HandleAsync(Msg("а это нормально?", "group", topicId: 7, replyToMessageId: 7, replyToUserId: 999));

        _gateway.Requests.Count.ShouldBe(1);
        _telegram.Sent.ShouldBeEmpty();
    }

    private static string ReadingAndQuestionJson(double glucose, string unclear = "") =>
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":" +
        glucose.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"unit\":\"mmol/L\",\"context\":\"fasting\"}],\"unclear\":[" + unclear + "],\"is_question\":true}";

    // Deliberate rule change: a message that is a reading AND an addressed question used to get only
    // the reaction ("A_message_with_readings_is_not_also_answered"); the question is now answered too.
    [Fact]
    public async Task An_addressed_message_with_a_reading_and_a_question_records_it_and_answers_once()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(ReadingAndQuestionJson(5.5), "haiku"));
        _gateway.Results.Enqueue(LlmResult.Answered("Тестовый ответ.", "sonnet"));
        var message = Msg("сахар 5.5 натощак, это нормально?");

        await HandleAsync(message);

        _gateway.Requests.Count.ShouldBe(2);
        _gateway.Requests[1].Tier.ShouldBe("smart");
        _events.Added.ShouldHaveSingleItem();
        _telegram.Reactions.ShouldBe(new[] { (111L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBe(new[] { (111L, (int?)null, "Тестовый ответ." + Footer, (int?)null) });
        _messages.OutgoingMessages.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_not_addressed_group_message_with_a_reading_and_a_question_is_recorded_without_an_answer()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(ReadingAndQuestionJson(5.5), "haiku"));
        var message = Msg("сахар 5.5 натощак, это нормально?", "group", topicId: 7);

        await HandleAsync(message);

        _gateway.Requests.Count.ShouldBe(1);
        _events.Added.ShouldHaveSingleItem();
        _telegram.Reactions.ShouldBe(new[] { (-100L, message.MessageId, (string?)WritingHand) });
        _telegram.Sent.ShouldBeEmpty();
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reading_and_a_question_that_triggers_an_alert_gets_only_the_alert()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(ReadingAndQuestionJson(2.5), "haiku"));

        await HandleAsync(Msg("сахар 2.5, что делать?"));

        _gateway.Requests.Count.ShouldBe(1);
        _events.Added.ShouldHaveSingleItem();
        SingleReply().ShouldBe(UrgentLow25);
    }

    [Fact]
    public async Task A_question_with_an_alert_that_could_not_be_sent_is_still_not_answered()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(ReadingAndQuestionJson(2.5), "haiku"));
        _telegram.ThrowOnSend = true;

        await Should.NotThrowAsync(() => HandleAsync(Msg("сахар 2.5, что делать?")));

        _gateway.Requests.Count.ShouldBe(1);
        _telegram.Sent.ShouldBeEmpty();
        _log.Entries.ShouldContain((LogLevel.Error, "Safety alert glucose.any for message 1 could not be sent"));
    }

    [Fact]
    public async Task A_reading_and_a_question_with_an_unclear_value_gets_only_the_clarification()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(
            ReadingAndQuestionJson(5.5, "{\"fragment\":\"18\",\"reason\":\"type\"}"), "haiku"));

        await HandleAsync(Msg("сахар 5.5, потом 18, это нормально?"));

        _gateway.Requests.Count.ShouldBe(1);
        _events.Added.ShouldHaveSingleItem();
        SingleReply().ShouldBe("Не понял «18» — уточните что это за показатель.");
    }

    [Fact]
    public async Task An_edit_with_a_reading_and_a_question_is_not_answered()
    {
        _gateway.Results.Enqueue(LlmResult.Answered(ReadingAndQuestionJson(5.5), "haiku"));

        await HandleAsync(Msg("сахар 5.5 натощак, это нормально?") with { IsEdit = true, EditedAt = Now }, StoreOutcome.Updated);

        _gateway.Requests.Count.ShouldBe(1);
        _telegram.Sent.ShouldBeEmpty();
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    // --- Hint for an addressed message that produced nothing ---

    private const string Hint = "Слушаю. Запишите показатель (например: сахар 5.8 после обеда) или задайте вопрос.";

    [Fact]
    public async Task An_addressed_group_greeting_gets_the_hint_as_a_reply()
    {
        var message = Msg("@test_health_bot привет", "group", topicId: 7);

        await HandleAsync(message);

        _telegram.Sent.ShouldBe(new[] { (-100L, (int?)7, Hint, (int?)message.MessageId) });
    }

    [Fact]
    public async Task The_hint_is_sent_once_per_five_minutes_per_place()
    {
        await HandleAsync(Msg("@test_health_bot привет", "group", topicId: 7));
        await HandleAsync(Msg("@test_health_bot привет", "group", topicId: 7), clock: new FixedClock(Now.AddMinutes(4)));
        _telegram.Sent.Count.ShouldBe(1);

        await HandleAsync(Msg("@test_health_bot привет", "group", topicId: 7), clock: new FixedClock(Now.AddMinutes(6)));
        _telegram.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_not_addressed_group_message_that_produced_nothing_stays_silent()
    {
        await HandleAsync(Msg("всем привет", "group", topicId: 7));

        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Private_chatter_gets_one_hint_then_silence_within_five_minutes()
    {
        await HandleAsync(Msg("понятно, спасибо"));
        await HandleAsync(Msg("понятно, спасибо"), clock: new FixedClock(Now.AddMinutes(1)));

        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe(Hint);
    }

    [Fact]
    public async Task A_recorded_reading_or_an_answered_question_gets_no_hint()
    {
        Answer(GlucoseAt930Json);
        await HandleAsync(Msg("сахар 7.8 в 9:30"));
        _telegram.Sent.ShouldBeEmpty();

        AskAndAnswer("Тестовый ответ.");
        await HandleAsync(Msg("какой сахар считается нормой?"));
        _telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Тестовый ответ." + Footer);
    }

    [Fact]
    public async Task An_edit_and_a_failed_extraction_get_no_hint()
    {
        await HandleAsync(Msg("понятно, спасибо") with { IsEdit = true, EditedAt = Now }, StoreOutcome.Updated);
        _telegram.Sent.ShouldBeEmpty();

        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.RateLimited);
        await HandleAsync(Msg("понятно, спасибо"));
        SingleReply().ShouldBe(ExtractionReplies.FailureNotice);
    }

    [Fact]
    public async Task A_question_with_a_clarification_is_not_also_answered()
    {
        _gateway.Results.Enqueue(LlmResult.Answered("{\"events\":[],\"unclear\":[{\"fragment\":\"18\",\"reason\":\"type\"}],\"is_question\":true}", "haiku"));

        await HandleAsync(Msg("утром было 18, это много?"));

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe("Не понял «18» — уточните что это за показатель.");
    }

    [Fact]
    public async Task A_question_with_a_quick_scan_alert_is_not_also_answered()
    {
        AskOnly();

        await HandleAsync(Msg("сахар 2.5, что делать?"));

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe(UrgentLow25 + NotRecorded);
    }

    [Fact]
    public async Task Edits_are_never_answered()
    {
        AskOnly();

        await HandleAsync(Msg("какой сахар считается нормой?") with { IsEdit = true, EditedAt = Now }, StoreOutcome.Updated);

        _gateway.Requests.Count.ShouldBe(1);
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_extraction_is_not_answered()
    {
        _gateway.NextResult = LlmResult.Refused(LlmRefusalReason.RateLimited);

        await HandleAsync(Msg("какой сахар считается нормой?"));

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe(ExtractionReplies.FailureNotice);
    }

    [Theory]
    [InlineData("Увеличьте дозу на 2 единицы.")]
    [InlineData("Сахар в норме. Можно убавить 2 ед перед ужином.")]
    [InlineData("Increase the dose by 2 units.")]
    public async Task Dose_advice_is_replaced_by_the_fixed_refusal(string modelAnswer)
    {
        AskAndAnswer(modelAnswer);

        await HandleAsync(Msg("на сколько увеличить дозу?"));

        SingleReply().ShouldBe(DoseRefusal + Footer);
        _messages.OutgoingMessages.ShouldHaveSingleItem().Text.ShouldBe(DoseRefusal + Footer);
        _telegram.Sent.ShouldAllBe(s => !s.Text.Contains(modelAnswer));
        _messages.OutgoingMessages.ShouldAllBe(m => !m.Text.Contains(modelAnswer));
        _log.Entries.ShouldContain(e => e.Message.Contains("dose_advice_replaced"));
        _log.Entries.ShouldAllBe(e => !e.Message.Contains(modelAnswer));
    }

    [Fact]
    public async Task Dose_advice_after_the_first_telegram_part_is_still_replaced()
    {
        const string doseSentence = "Увеличьте дозу на 2 единицы.";
        var longAnswer = string.Concat(Enumerable.Repeat("Сахар в норме, записи за день выглядят ровно. ", 120));
        longAnswer.Length.ShouldBeGreaterThan(4096);
        AskAndAnswer(longAnswer + doseSentence);

        await HandleAsync(Msg("расскажите подробно про сахар"));

        SingleReply().ShouldBe(DoseRefusal + Footer);
        _telegram.Sent.ShouldAllBe(s => !s.Text.Contains(doseSentence));
        _messages.OutgoingMessages.ShouldAllBe(m => !m.Text.Contains(doseSentence));
    }

    [Theory]
    [InlineData(LlmRefusalReason.RateLimited, "Слишком много запросов, подождите минуту.")]
    [InlineData(LlmRefusalReason.DailyCapReached, "Дневной лимит запросов исчерпан, продолжим завтра.")]
    [InlineData(LlmRefusalReason.AllModelsUnavailable, "Все модели сейчас недоступны (лимиты), попробуйте позже.")]
    [InlineData(LlmRefusalReason.BudgetExhausted, "Лимит расходов исчерпан, попробуйте позже.")]
    [InlineData(LlmRefusalReason.Failed, "Не получилось ответить, попробуйте ещё раз.")]
    [InlineData(LlmRefusalReason.NotConfigured, "Ассистент пока не настроен.")]
    public async Task Refusals_reply_with_the_general_texts(LlmRefusalReason reason, string expected)
    {
        AskOnly();
        _gateway.Results.Enqueue(LlmResult.Refused(reason));

        await HandleAsync(Msg("какой сахар считается нормой?"));

        SingleReply().ShouldBe(expected);
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_throwing_smart_call_gets_the_failed_text()
    {
        AskOnly();
        _gateway.ThrowOnCall[2] = new InvalidOperationException("simulated");

        await Should.NotThrowAsync(() => HandleAsync(Msg("какой сахар считается нормой?")));

        _gateway.Requests.Count.ShouldBe(2);
        SingleReply().ShouldBe(FailedText);
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_context_read_gets_the_failed_text()
    {
        AskOnly();
        _profiles.ThrowOnGetRules = true;

        // No digits: neither the save block nor the quick scan reads the rules.
        await Should.NotThrowAsync(() => HandleAsync(Msg("какой сахар считается нормой?")));

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe(FailedText);
    }

    [Fact]
    public async Task An_empty_answer_gets_the_failed_text()
    {
        AskAndAnswer("   ");

        await HandleAsync(Msg("какой сахар считается нормой?"));

        SingleReply().ShouldBe(FailedText);
        _messages.OutgoingMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_answer_prompt_says_not_configured()
    {
        _prompts.AnswerPrompt = null;
        AskOnly();

        await HandleAsync(Msg("какой сахар считается нормой?"));

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe("Ассистент пока не настроен.");
    }

    [Fact]
    public async Task Llm_config_off_says_not_configured()
    {
        AskOnly();

        await CreateAssistant(llmOff: true).HandleAsync(
            Bot, _telegram, Msg("какой сахар считается нормой?"), new StoreResult(StoreOutcome.Stored, 1), CancellationToken.None);

        _gateway.Requests.Count.ShouldBe(1);
        SingleReply().ShouldBe("Ассистент пока не настроен.");
    }

    [Fact]
    public async Task Logs_never_contain_the_question_or_the_answer()
    {
        AskAndAnswer("секретный тестовый ответ");

        await HandleAsync(Msg("секретный тестовый вопрос?"));

        _telegram.Sent.ShouldHaveSingleItem();
        _log.Entries.ShouldAllBe(e => !e.Message.Contains("секретный"));
        _log.Entries.ShouldContain(e => e.Message.Contains("Answer answered for message 1"));
    }

    private sealed class CapturingLogger : ILogger<HealthAssistant>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
