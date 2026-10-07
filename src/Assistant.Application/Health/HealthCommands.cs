using System.Globalization;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>The health bot's deterministic `/…` commands: profile and thresholds (owner-only
/// changes), /today, /notes, /week, /undo, /del, /version and /start. No model call.</summary>
internal sealed class HealthCommands
{
    private const int MaxPhoneLength = 100;
    private const int MaxNoteLength = 500;

    private static readonly string[] StartDateFormats = { "dd.MM.yyyy", "d.M.yyyy" };

    private const string StartText =
        "Привет! Я веду дневник здоровья одного участника семьи. Пишите показатели обычным текстом " +
        "(например, «сахар 5.6 натощак» или «давление 120/80») — я запишу их и поставлю ✍ на сообщение. " +
        "Опасные значения и симптомы я сразу отмечаю фиксированным предупреждением по порогам из /thresholds " +
        "(пока врач их не подтвердил, они помечены «не подтверждено врачом»). Я не заменяю врача: " +
        "если самочувствие вызывает тревогу, звоните врачу или в скорую, не дожидаясь меня. " +
        "Можно попросить консультацию: в личном чате просто напишите, " +
        "в группе — упомяните меня, ответьте на моё сообщение или включите ответы для этого места.\n" +
        "/today — записи за сегодня\n" +
        "/notes — последние заметки или /notes <тег>\n" +
        "/undo — отменить вашу последнюю запись\n" +
        "/del — удалить записи (в ответ на сообщение) или /del <номер>\n" +
        "/week — текущая неделя\n" +
        "/profile — профиль\n" +
        "/thresholds — пороги\n" +
        "/setprofile <поле> <текст> — состояние, лекарства, аллергии, план или врач (владелец)\n" +
        "/version — версия\n" +
        "Владелец семьи: /setstart ДД.ММ.ГГГГ — начало отсчёта, /settz Area/City — часовой пояс, " +
        "/setphone — телефон для экстренных случаев, /setnote — заметка с контекстом для ответов на вопросы, " +
        "/threshold — пороги от врача.";

    private const string ThresholdsHeader = "Пороги (глюкоза в ммоль/л, давление в мм рт. ст.):";

    private const string ThresholdsFooter =
        "Изменить (владелец): /threshold <правило> <поле> <значение> — вводите значения, которые дал врач. " +
        "Вернуть по умолчанию: /threshold <правило> default.";

    private const string SetStartUsageText = "Укажите дату начала отсчёта: /setstart ДД.ММ.ГГГГ";
    private const string SetStartRangeText = "Дата должна быть не позже сегодняшней и не раньше чем 300 дней назад.";
    private const string SetTimeZoneUsageText = "Укажите часовой пояс: /settz Area/City, например /settz Europe/Berlin";
    private const string SetPhoneUsageText = "Укажите номер для экстренных случаев: /setphone <текст> (до 100 символов).";
    private const string SetNoteUsageText = "Укажите заметку (до 500 символов): /setnote <текст>; /setnote - удаляет её.";

    private const string ThresholdUsageText =
        "Формат: /threshold <правило> <поле> <значение> — вводите значения, которые дал врач; " +
        "/threshold <правило> default — вернуть значения по умолчанию. Правила и поля: /thresholds.";

    private const string TodayEmptyText = "Сегодня записей нет.";
    private const string NotesUsageText = "Формат: /notes или /notes <тег> (до 32 букв).";
    private const string NotesEmptyText = "Заметок нет.";
    internal const string NothingToUndoText = "Нечего отменять.";
    private const string EventNotFoundText = "Не нашёл такую запись.";
    private const string DeleteUsageText =
        "Формат: /del в ответ на сообщение с показателями или /del <номер записи> (номера — в /today).";

    /// <summary>/undo (and the free-text undo) only reach records created this recently.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    private readonly IHealthProfileStore _profiles;
    private readonly IFamilyOwnership _ownership;
    private readonly IEventStore _events;
    private readonly HealthReplies _replies;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;

    public HealthCommands(
        IHealthProfileStore profiles, IFamilyOwnership ownership, IEventStore events, HealthReplies replies, IClock clock, BuildInfo buildInfo)
    {
        _profiles = profiles;
        _ownership = ownership;
        _events = events;
        _replies = replies;
        _clock = clock;
        _buildInfo = buildInfo;
    }

    public async Task HandleAsync(
        string command, string? args, ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId,
        HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "start" when message.ChatType == "private":
                await _replies.ReplyAsync(telegramClient, message, StartText, cancellationToken);
                return;

            case "week":
                await _replies.ReplyAsync(telegramClient, message, $"Неделя: {CurrentWeek(profile).Describe()}", cancellationToken);
                return;

            case "profile":
                await _replies.ReplyAsync(telegramClient, message, await DescribeProfileAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "thresholds":
                await _replies.ReplyAsync(telegramClient, message, await DescribeThresholdsAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "today":
                await _replies.ReplyAsync(telegramClient, message, await DescribeTodayAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "notes":
                await NotesAsync(telegramClient, message, familyId, profile, args, cancellationToken);
                return;

            case "undo":
                await UndoAsync(bot, telegramClient, message, familyId, profile, cancellationToken);
                return;

            case "del":
                await DeleteAsync(bot, telegramClient, message, familyId, profile, args, cancellationToken);
                return;

            case "version":
                await _replies.ReplyAsync(telegramClient, message, VersionText.Format(_buildInfo, _clock.UtcNow), cancellationToken);
                return;

            case "setstart":
            case "settz":
            case "setphone":
            case "setnote":
            case "setprofile":
            case "threshold":
                await HandleOwnerCommandAsync(command, familyId, profile, telegramClient, message, args, cancellationToken);
                return;

            default:
                return; // Unknown command, or /start in a group: silent.
        }
    }

    private async Task HandleOwnerCommandAsync(
        string command, long familyId, HealthProfileInfo profile, ITelegramClient telegramClient, IncomingMessage message, string? args,
        CancellationToken cancellationToken)
    {
        // Re-read on every command; checked before the arguments, so a non-owner learns nothing.
        if (message.UserId is not { } userId || !await _ownership.IsApprovedOwnerAsync(familyId, userId, cancellationToken))
        {
            await _replies.ReplyAsync(telegramClient, message, HealthAssistant.OwnerOnlyText, cancellationToken);
            return;
        }

        var reply = command switch
        {
            "setstart" => await SetStartAsync(familyId, profile, args, userId, cancellationToken),
            "settz" => await SetTimeZoneAsync(familyId, profile, args, userId, cancellationToken),
            "setphone" => await SetPhoneAsync(familyId, profile, args, userId, cancellationToken),
            "setnote" => await SetNoteAsync(familyId, profile, args, userId, cancellationToken),
            "setprofile" => await SetProfileFieldAsync(familyId, profile, args, userId, cancellationToken),
            _ => await SetThresholdAsync(familyId, profile, args, userId, cancellationToken)
        };
        await _replies.ReplyAsync(telegramClient, message, reply, cancellationToken);
    }

    private StageWeekResult CurrentWeek(HealthProfileInfo profile) =>
        StageWeek.Compute(ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone), profile.StageStartDate);

    private async Task<string> DescribeProfileAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        var doctorRules = rules.Count(r => r.Source == SafetyRuleSources.Doctor);
        var start = profile.StageStartDate is { } date
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : "не задано (/setstart)";
        var note = string.IsNullOrEmpty(profile.ContextNote)
            ? "не задана (/setnote)"
            : profile.ContextNote;
        return "Профиль:\n" +
               $"Начало отсчёта: {start}\n" +
               $"Неделя: {CurrentWeek(profile).Describe()}\n" +
               $"Часовой пояс: {profile.TimeZone}\n" +
               $"Телефон для экстренных случаев: {profile.EmergencyPhone}\n" +
               $"Заметка: {note}\n" +
               $"Состояние: {profile.Conditions ?? "не задано"}\n" +
               $"Лекарства: {profile.Medications ?? "не задано"}\n" +
               $"Аллергии: {profile.Allergies ?? "не задано"}\n" +
               $"План врача: {profile.DoctorPlan ?? "не задано"}\n" +
               $"Врач: {profile.DoctorContacts ?? "не задано"}\n" +
               $"Пороги: правил {rules.Count}, от врача {doctorRules} (/thresholds)";
    }

    private async Task<string> DescribeThresholdsAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        return $"{ThresholdsHeader}\n{string.Join("\n", rules.Select(SafetyRuleText.Format))}\n\n{ThresholdsFooter}";
    }

    private async Task<string> SetProfileFieldAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        const string usage = "Формат: /setprofile <поле> <текст> (до 1000 символов); поля: состояние, лекарства, аллергии, план, врач. Текст - очищает поле.";
        var trimmed = args?.Trim();
        var split = trimmed is null ? -1 : Array.FindIndex(trimmed.ToCharArray(), char.IsWhiteSpace);
        if (split < 1) return usage;
        var field = trimmed![..split].ToLowerInvariant() switch
        {
            "состояние" => HealthProfileField.Conditions,
            "лекарства" => HealthProfileField.Medications,
            "аллергии" => HealthProfileField.Allergies,
            "план" => HealthProfileField.DoctorPlan,
            "врач" => HealthProfileField.DoctorContacts,
            _ => (HealthProfileField?)null
        };
        var value = trimmed[(split + 1)..].Trim();
        if (field is null || value.Length is 0 or > 1000) return usage;
        await _profiles.SaveFieldAsync(familyId, profile.Id, field.Value, value == "-" ? null : value, userId, cancellationToken);
        return value == "-" ? "Поле профиля очищено." : "Поле профиля сохранено.";
    }

    private async Task<string> SetStartAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null || !DateOnly.TryParseExact(args, StartDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return SetStartUsageText;
        }

        var week = StageWeek.Compute(ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone), start);
        if (week.Status != StageWeekStatus.Valid)
        {
            return SetStartRangeText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { StageStartDate = start }, userId, cancellationToken);
        return $"Начало отсчёта: {start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}. Неделя: {week.Describe()}";
    }

    private async Task<string> SetTimeZoneAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null)
        {
            return SetTimeZoneUsageText;
        }

        if (!ProfileTimeZone.TryNormalize(args, out var zoneId))
        {
            return $"Неизвестный часовой пояс: {args}. {SetTimeZoneUsageText}";
        }

        await _profiles.SaveProfileAsync(familyId, profile with { TimeZone = zoneId }, userId, cancellationToken);
        return $"Часовой пояс: {zoneId}.";
    }

    private async Task<string> SetPhoneAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null || args.Length > MaxPhoneLength)
        {
            return SetPhoneUsageText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { EmergencyPhone = args }, userId, cancellationToken);
        return $"Телефон для экстренных случаев: {args}";
    }

    private async Task<string> SetNoteAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        if (args is null)
        {
            return SetNoteUsageText;
        }

        if (args == "-")
        {
            await _profiles.SaveProfileAsync(familyId, profile with { ContextNote = null }, userId, cancellationToken);
            return "Заметка удалена.";
        }

        if (args.Length > MaxNoteLength)
        {
            return SetNoteUsageText;
        }

        await _profiles.SaveProfileAsync(familyId, profile with { ContextNote = args }, userId, cancellationToken);
        return "Заметка сохранена.";
    }

    private async Task<string> SetThresholdAsync(long familyId, HealthProfileInfo profile, string? args, long userId, CancellationToken cancellationToken)
    {
        var parts = args?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        if (parts.Length is not (2 or 3))
        {
            return ThresholdUsageText;
        }

        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        var current = rules.FirstOrDefault(r => string.Equals(r.RuleKey, parts[0], StringComparison.OrdinalIgnoreCase));
        var seed = current is null ? null : SafetyRuleDefaults.Find(current.RuleKey);
        if (current is null || seed is null)
        {
            return $"Нет такого правила: {parts[0]}. Список: /thresholds.";
        }

        if (parts.Length == 2)
        {
            if (!string.Equals(parts[1], "default", StringComparison.OrdinalIgnoreCase))
            {
                return ThresholdUsageText;
            }

            await _profiles.SaveRuleAsync(familyId, profile.Id, seed, userId, cancellationToken);
            return $"Восстановлены значения по умолчанию: {SafetyRuleText.Format(seed)}";
        }

        var edit = SafetyRuleEditor.SetField(current, parts[1], parts[2]);
        if (edit.Rule is null)
        {
            return edit.Error ?? ThresholdUsageText;
        }

        await _profiles.SaveRuleAsync(familyId, profile.Id, edit.Rule, userId, cancellationToken);
        return $"Сохранено: {SafetyRuleText.Format(edit.Rule)}";
    }

    private async Task<string> DescribeTodayAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var today = ProfileTimeZone.LocalToday(_clock.UtcNow, profile.TimeZone);
        var from = ProfileTimeZone.StartOfDayUtc(today, profile.TimeZone);
        var to = ProfileTimeZone.StartOfDayUtc(today.AddDays(1), profile.TimeZone);
        var events = await _events.GetActiveAsync(familyId, profile.Id, from, to, cancellationToken);
        if (events.Count == 0)
        {
            return TodayEmptyText;
        }

        var zone = ProfileTimeZone.Find(profile.TimeZone);
        return $"Сегодня, {today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}:\n" +
               string.Join("\n", events.Select(e => HealthEventText.Line(e, zone)));
    }

    private async Task NotesAsync(
        ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        string? args, CancellationToken cancellationToken)
    {
        string? tag = null;
        if (args is not null)
        {
            var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 1 || !HealthNoteTags.TryNormalize(parts[0], out tag))
            {
                await _replies.ReplyAsync(telegramClient, message, NotesUsageText, cancellationToken);
                return;
            }
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var notes = await _events.GetNotesAsync(familyId, profile.Id, tag, now, tag is null ? 10 : 20, cancellationToken);
        var zone = ProfileTimeZone.Find(profile.TimeZone);
        var text = notes.Count == 0 ? NotesEmptyText
            : "Заметки:\n" + string.Join("\n", notes.Select(note =>
                $"#{note.Id.ToString(CultureInfo.InvariantCulture)} "
                + TimeZoneInfo.ConvertTime(note.OccurredAt, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                + " " + HealthEventText.Describe(note)));
        await _replies.ReplyAsync(telegramClient, message, text, cancellationToken);
    }

    private async Task UndoAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile,
        CancellationToken cancellationToken)
    {
        var deleted = message.UserId is { } userId
            ? await _events.DeleteLatestOfUserAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, message.TopicId, userId, _clock.UtcNow - UndoWindow,
                EventDeleteReasons.Undo, cancellationToken)
            : DeletedEvents.None;
        if (deleted.Events.Count == 0)
        {
            await _replies.ReplyAsync(telegramClient, message, NothingToUndoText, cancellationToken);
            return;
        }

        await _replies.ClearReactionsAsync(telegramClient, deleted.MessagesWithoutEvents, cancellationToken);
        await _replies.ReplyAsync(telegramClient, message, HealthReplies.DeletedText(deleted), cancellationToken);
    }

    private async Task DeleteAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, long familyId, HealthProfileInfo profile, string? args,
        CancellationToken cancellationToken)
    {
        DeletedEvents deleted;
        if (args is null)
        {
            // A reply to the forum topic root is how every topic message looks; it is not a reply.
            var isReplyToTopicRoot = message.TopicId is { } topicId && message.ReplyToMessageId == topicId;
            if (message.ReplyToMessageId is not { } repliedTo || isReplyToTopicRoot)
            {
                await _replies.ReplyAsync(telegramClient, message, DeleteUsageText, cancellationToken);
                return;
            }

            deleted = await _events.DeleteBySourceTelegramMessageAsync(
                familyId, profile.Id, bot.TelegramBotId, message.ChatId, repliedTo, EventDeleteReasons.Del, cancellationToken);
        }
        else
        {
            var idText = args.StartsWith('#') ? args[1..] : args;
            if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId) || eventId <= 0)
            {
                await _replies.ReplyAsync(telegramClient, message, DeleteUsageText, cancellationToken);
                return;
            }

            deleted = await _events.DeleteByIdAsync(familyId, profile.Id, eventId, EventDeleteReasons.Del, cancellationToken);
        }

        if (deleted.Events.Count == 0)
        {
            await _replies.ReplyAsync(telegramClient, message, EventNotFoundText, cancellationToken);
            return;
        }

        await _replies.ClearReactionsAsync(telegramClient, deleted.MessagesWithoutEvents, cancellationToken);
        await _replies.ReplyAsync(telegramClient, message, HealthReplies.DeletedText(deleted), cancellationToken);
    }
}
