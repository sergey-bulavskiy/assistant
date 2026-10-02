using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Application.Health;

public class HealthAssistantTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-02-07T10:00:00Z");
    private static readonly ReceivingBot Bot = new(BotDbId: 1, TelegramBotId: 999, Username: "test_health_bot", FamilyId: 42, Role: "health");

    private const string OwnerOnly = "Только владелец семьи может менять профиль.";

    private readonly FakeHealthProfileStore _profiles = new();
    private readonly FakeFamilyOwnership _ownership = new();
    private readonly FakeTelegramClient _telegram = new();
    private int _nextMessageId = 100;

    public HealthAssistantTests()
    {
        _ownership.OwnerUserIds.Add(111);
    }

    private HealthAssistant CreateAssistant(IClock? clock = null) =>
        new(_profiles, _ownership, clock ?? new FixedClock(Now), new BuildInfo("abcdef1234", null, Now.AddHours(-1)), NullLogger<HealthAssistant>.Instance);

    private IncomingMessage Msg(string? text, string chatType = "private", long userId = 111, int? topicId = null, MessageKind kind = MessageKind.Text) =>
        new(ChatId: chatType == "private" ? userId : -100, ChatType: chatType, ChatTitle: chatType == "private" ? null : "test group",
            TopicId: topicId, MessageId: _nextMessageId++, UserId: userId, Username: "test_user",
            Text: text, Kind: kind, IsEdit: false, SentAt: Now, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    private Task HandleAsync(IncomingMessage message, StoreOutcome outcome = StoreOutcome.Stored, IClock? clock = null) =>
        CreateAssistant(clock).HandleAsync(Bot, _telegram, message, new StoreResult(outcome, 1), CancellationToken.None);

    private string SingleReply() => _telegram.Sent.ShouldHaveSingleItem().Text;

    [Theory]
    [InlineData(StoreOutcome.Updated)]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Only_new_messages_are_handled(StoreOutcome outcome)
    {
        await HandleAsync(Msg("/week"), outcome);

        _telegram.Sent.ShouldBeEmpty();
        _profiles.GetOrCreateCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public async Task Plain_text_creates_the_profile_and_gets_no_reply(string chatType)
    {
        await HandleAsync(Msg("test message", chatType));

        _telegram.Sent.ShouldBeEmpty();
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
        foreach (var expected in new[] { "/week", "/profile", "/thresholds", "/setstart", "/threshold", "не проверяю", "не советую лекарства" })
        {
            reply.ShouldContain(expected);
        }

        _telegram.Sent.Clear();
        await HandleAsync(Msg("/start", "group"));
        _telegram.Sent.ShouldBeEmpty();
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
}
