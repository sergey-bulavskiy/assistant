using System.Collections.Concurrent;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Assistant.Infrastructure.Families;

/// <summary>Drives the two DM-with-buttons approval flows (place, user). Always operates via
/// IgnoreQueryFilters — it is called from a role bot's request scope (a concrete family) but reads
/// and writes rows across the manager's own reach (sending DMs through the manager's client), so it
/// deliberately does not rely on ICurrentFamily at all.</summary>
public class ApprovalService : IApprovalService
{
    // approvalKey ("place"/placeId or "member"/memberId) -> owner DM locations. Best-effort,
    // in-memory only (Judgment Call 5): if the process restarts between sending and resolving, a
    // redundant tap still answers correctly, it just can't visually disable the other owners'
    // buttons any more.
    private static readonly ConcurrentDictionary<(string Kind, long Id), List<(long ChatId, int MessageId)>> SentMessages = new();

    private readonly AssistantDbContext _db;
    private readonly ITelegramClientFactory _clientFactory;
    private readonly IOptions<BotOptions> _options;
    private readonly IClock _clock;

    public ApprovalService(AssistantDbContext db, ITelegramClientFactory clientFactory, IOptions<BotOptions> options, IClock clock)
    {
        _db = db;
        _clientFactory = clientFactory;
        _options = options;
        _clock = clock;
    }

    public async Task<long> GetOrCreatePendingPlaceAsync(long botDbId, long chatId, int? topicId, string title, CancellationToken cancellationToken)
    {
        var existing = await _db.Places.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.BotId == botDbId && p.ChatId == chatId && p.TopicId == topicId, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var bot = await _db.Bots.IgnoreQueryFilters().SingleAsync(b => b.Id == botDbId, cancellationToken);

        var place = new Place
        {
            BotId = botDbId,
            ChatId = chatId,
            TopicId = topicId,
            Title = title,
            Status = PlaceStatus.Pending,
            CreatedAt = _clock.UtcNow
        };
        _db.Places.Add(place);
        await _db.SaveChangesAsync(cancellationToken);

        var text = topicId is null
            ? $"Бот @{bot.Username} добавлен в «{title}». Работать здесь?"
            : $"Первое сообщение в теме «{title}» (бот @{bot.Username}). Работать здесь?";
        var buttons = new[]
        {
            new InlineButton("Да", $"place_approve:{place.Id}"),
            new InlineButton("Нет", $"place_deny:{place.Id}")
        };

        await SendToOwnersAsync(bot.FamilyId, ("place", place.Id), text, buttons, cancellationToken);

        return place.Id;
    }

    public async Task<ApprovalResolution> ResolvePlaceApprovalAsync(long placeId, bool approve, CancellationToken cancellationToken)
    {
        var place = await _db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == placeId, cancellationToken);
        if (place is null || place.Status != PlaceStatus.Pending)
        {
            return ApprovalResolution.AlreadyResolved;
        }

        place.Status = approve ? PlaceStatus.Approved : PlaceStatus.Denied;
        await _db.SaveChangesAsync(cancellationToken);

        await UpdateSentMessagesAsync(("place", placeId), approve ? "Одобрено ✅" : "Отклонено ❌", cancellationToken);
        return ApprovalResolution.Applied;
    }

    public async Task<long> GetOrCreatePendingFamilyMemberAsync(
        long familyId, long telegramUserId, string displayName, string? username, string requestingBotUsername, CancellationToken cancellationToken)
    {
        var existing = await _db.FamilyMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.TelegramUserId == telegramUserId, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = _clock.UtcNow;
        var member = new FamilyMember
        {
            FamilyId = familyId,
            TelegramUserId = telegramUserId,
            DisplayName = displayName,
            Username = username,
            Status = FamilyMemberStatus.Pending,
            IsOwner = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.FamilyMembers.Add(member);
        await _db.SaveChangesAsync(cancellationToken);

        var usernameSuffix = username is null ? string.Empty : $" (@{username})";
        var text = $"{displayName}{usernameSuffix} хочет пользоваться ботом @{requestingBotUsername}. Разрешить?";
        var buttons = new[]
        {
            new InlineButton("Разрешить", $"member_allow:{member.Id}"),
            new InlineButton("Отклонить", $"member_deny:{member.Id}")
        };

        await SendToOwnersAsync(familyId, ("member", member.Id), text, buttons, cancellationToken);

        return member.Id;
    }

    public async Task<ApprovalResolution> ResolveUserApprovalAsync(long familyMemberId, bool approve, CancellationToken cancellationToken)
    {
        var member = await _db.FamilyMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == familyMemberId, cancellationToken);
        if (member is null || member.Status != FamilyMemberStatus.Pending)
        {
            return ApprovalResolution.AlreadyResolved;
        }

        member.Status = approve ? FamilyMemberStatus.Approved : FamilyMemberStatus.Denied;
        member.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await UpdateSentMessagesAsync(("member", familyMemberId), approve ? "Разрешено ✅" : "Отклонено ❌", cancellationToken);
        return ApprovalResolution.Applied;
    }

    private async Task SendToOwnersAsync(
        long? familyId, (string Kind, long Id) approvalKey, string text, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        if (familyId is null)
        {
            return;
        }

        var owners = await _db.FamilyMembers.IgnoreQueryFilters()
            .Where(m => m.FamilyId == familyId && m.IsOwner && m.Status == FamilyMemberStatus.Approved)
            .ToListAsync(cancellationToken);

        var managerClient = _clientFactory.Create(_options.Value.ManagerToken);
        var locations = new List<(long ChatId, int MessageId)>();

        foreach (var owner in owners)
        {
            var messageId = await managerClient.SendTextWithButtonsAsync(owner.TelegramUserId, null, text, buttons, cancellationToken);
            locations.Add((owner.TelegramUserId, messageId));
        }

        SentMessages[approvalKey] = locations;
    }

    private async Task UpdateSentMessagesAsync((string Kind, long Id) approvalKey, string resolutionText, CancellationToken cancellationToken)
    {
        if (!SentMessages.TryRemove(approvalKey, out var locations))
        {
            return;
        }

        var managerClient = _clientFactory.Create(_options.Value.ManagerToken);
        foreach (var (chatId, messageId) in locations)
        {
            await managerClient.EditMessageTextAsync(chatId, messageId, resolutionText, cancellationToken);
        }
    }
}
