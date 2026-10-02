using System.Globalization;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>The `health` role bot: a health tracking assistant for one household member (one profile
/// per bot, created lazily with the default safety rules). Answers its deterministic commands;
/// ordinary text is stored by UpdateHandler and otherwise left alone for now. Never logs message text.</summary>
public class HealthAssistant : IHealthAssistant
{
    public const string OwnerOnlyText = "Только владелец семьи может менять профиль.";

    public const string NonTextText = "Голосовые и фото пока не поддерживаются — напишите текстом.";

    private const int MaxPhoneLength = 100;
    private const int MaxNoteLength = 500;

    private static readonly string[] StartDateFormats = { "dd.MM.yyyy", "d.M.yyyy" };

    private const string StartText =
        "Привет! Я веду дневник здоровья одного участника семьи. Пока я понимаю только команды: " +
        "показатели из сообщений ещё не записываю и значения не проверяю. Не полагайтесь на меня, " +
        "если самочувствие вызывает тревогу, — звоните врачу или в скорую. " +
        "Я никогда не советую лекарства и дозы инсулина.\n" +
        "/week — текущий срок\n" +
        "/profile — профиль\n" +
        "/thresholds — пороги\n" +
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

    private readonly IHealthProfileStore _profiles;
    private readonly IFamilyOwnership _ownership;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly ILogger<HealthAssistant> _logger;

    public HealthAssistant(
        IHealthProfileStore profiles,
        IFamilyOwnership ownership,
        IClock clock,
        BuildInfo buildInfo,
        ILogger<HealthAssistant> logger)
    {
        _profiles = profiles;
        _ownership = ownership;
        _clock = clock;
        _buildInfo = buildInfo;
        _logger = logger;
    }

    public async Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken)
    {
        // Only new messages. Edits (Updated) keep today's storage behaviour and nothing else for now;
        // redeliveries never reply twice.
        if (storeResult.Outcome != StoreOutcome.Stored || message.IsEdit || bot.FamilyId is not { } familyId)
        {
            return;
        }

        if (message.Kind == MessageKind.Service)
        {
            return;
        }

        if (message.Kind != MessageKind.Text || message.Text is not { } text)
        {
            // Voice and photos are not supported yet: say so in private chats, stay silent in groups.
            if (message.ChatType == "private")
            {
                await ReplyAsync(telegramClient, message, NonTextText, cancellationToken);
            }

            return;
        }

        // The profile (with its default rules) is created lazily by the bot's first text message.
        var profile = await _profiles.GetOrCreateAsync(familyId, bot.BotDbId, cancellationToken);

        var command = CommandParser.Parse(text, bot.Username);
        if (command is null)
        {
            return; // Plain text (stored only) or /cmd@otherbot.
        }

        var args = CommandParser.ParseArgs(text);
        switch (command)
        {
            case "start" when message.ChatType == "private":
                await ReplyAsync(telegramClient, message, StartText, cancellationToken);
                return;

            case "week":
                await ReplyAsync(telegramClient, message, $"Срок: {CurrentWeek(profile).Describe()}", cancellationToken);
                return;

            case "profile":
                await ReplyAsync(telegramClient, message, await DescribeProfileAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "thresholds":
                await ReplyAsync(telegramClient, message, await DescribeThresholdsAsync(familyId, profile, cancellationToken), cancellationToken);
                return;

            case "version":
                await ReplyAsync(telegramClient, message, VersionText.Format(_buildInfo, _clock.UtcNow), cancellationToken);
                return;

            case "setstart":
            case "settz":
            case "setphone":
            case "setnote":
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
            await ReplyAsync(telegramClient, message, OwnerOnlyText, cancellationToken);
            return;
        }

        var reply = command switch
        {
            "setstart" => await SetStartAsync(familyId, profile, args, userId, cancellationToken),
            "settz" => await SetTimeZoneAsync(familyId, profile, args, userId, cancellationToken),
            "setphone" => await SetPhoneAsync(familyId, profile, args, userId, cancellationToken),
            _ => await SetNoteAsync(familyId, profile, args, userId, cancellationToken)
        };
        await ReplyAsync(telegramClient, message, reply, cancellationToken);
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
            ? "не задана (/setnote) — без неё ответы на вопросы не знают контекста"
            : profile.ContextNote;
        return "Профиль:\n" +
               $"Начало отсчёта: {start}\n" +
               $"Срок: {CurrentWeek(profile).Describe()}\n" +
               $"Часовой пояс: {profile.TimeZone}\n" +
               $"Телефон для экстренных случаев: {profile.EmergencyPhone}\n" +
               $"Заметка: {note}\n" +
               $"Пороги: правил {rules.Count}, от врача {doctorRules} (/thresholds)";
    }

    private async Task<string> DescribeThresholdsAsync(long familyId, HealthProfileInfo profile, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        return $"{ThresholdsHeader}\n{string.Join("\n", rules.Select(SafetyRuleText.Format))}\n\n{ThresholdsFooter}";
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
        return $"Начало отсчёта: {start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}. Срок: {week.Describe()}";
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

    // Same convention as the General assistant: a Telegram reply in groups, a plain message in
    // private chats. Fixed bot texts, so never stored as conversation.
    private async Task ReplyAsync(ITelegramClient telegramClient, IncomingMessage message, string text, CancellationToken cancellationToken)
    {
        try
        {
            var replyToMessageId = message.ChatType == "private" ? (int?)null : message.MessageId;
            await telegramClient.SendTextAsync(message.ChatId, message.TopicId, text, replyToMessageId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("failed to send health reply: {ExceptionType}", ex.GetType().Name);
        }
    }
}
