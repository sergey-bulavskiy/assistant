using Assistant.Application.Common;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Assistant.Infrastructure.Llm;

public interface IBudgetNoticeSender
{
    Task NotifyAsync(BudgetStatus status, CancellationToken cancellationToken);
}

/// <summary>Sends one DM per (period, state) the FIRST time it's reached, to every platform admin
/// (spec §10.3: owners of the first-claimed family), through the manager bot's own client -- same
/// insert-then-send pattern as ApprovalService (Verified facts §D.4). Never blocks or fails the LLM
/// call it's attached to: any exception here is logged (type only) and swallowed, not rethrown.
///
/// Deviation from the plan's Step 2 code (execution notes "Reaffirms: Tasks 7-8 must use
/// BudgetState, not rounded Percent"): the plan's WarnThresholdFor/HardThresholdFor derived the
/// dedup "threshold" from BudgetPeriodStatus.Percent, which is display-only and keeps climbing call
/// after call -- its own "Pitfall" paragraph already flagged this as wrong (it would insert a new
/// row, and send a new DM, for every distinct percent value past the warn line, not once per
/// threshold). BudgetState.Warn/Soft/Hard already are the fixed, finite set of thresholds the spec
/// means ("one warning DM per period per threshold"); their numeric enum value is stored as
/// BudgetNotice.Threshold, so the unique (kind, start, threshold) index dedups on the state actually
/// crossed, not on a continuously-varying percent.</summary>
public class BudgetNoticeSender : IBudgetNoticeSender
{
    private readonly AssistantDbContext _db;
    private readonly ITelegramClientFactory _clientFactory;
    private readonly IOptions<BotOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<BudgetNoticeSender> _logger;

    public BudgetNoticeSender(
        AssistantDbContext db, ITelegramClientFactory clientFactory, IOptions<BotOptions> options, IClock clock,
        ILogger<BudgetNoticeSender> logger)
    {
        _db = db;
        _clientFactory = clientFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task NotifyAsync(BudgetStatus status, CancellationToken cancellationToken)
    {
        try
        {
            await NotifyPeriodAsync(status.Daily, cancellationToken);
            await NotifyPeriodAsync(status.Monthly, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("BudgetNoticeSender failed: {ExceptionType}", ex.GetType().Name);
        }
    }

    private async Task NotifyPeriodAsync(BudgetPeriodStatus period, CancellationToken cancellationToken)
    {
        var threshold = ThresholdFor(period.State);
        if (threshold is null)
        {
            return; // Normal: nothing to notify.
        }

        if (!await TryInsertNoticeAsync(period.Kind, period.PeriodStart, threshold.Value, cancellationToken))
        {
            return; // someone else already sent this (period, state) notice.
        }

        var admins = await PlatformAdmins.GetAsync(_db, cancellationToken);
        if (admins.Count == 0)
        {
            return;
        }

        var text = BuildText(period);
        var managerClient = _clientFactory.Create(_options.Value.ManagerToken);
        foreach (var admin in admins)
        {
            await managerClient.SendTextAsync(admin.TelegramUserId, null, text, replyToMessageId: null, cancellationToken);
        }
    }

    private static int? ThresholdFor(BudgetState state) => state switch
    {
        BudgetState.Warn => (int)BudgetState.Warn,
        BudgetState.Soft => (int)BudgetState.Soft,
        BudgetState.Hard => (int)BudgetState.Hard,
        _ => null
    };

    private static string PeriodLabel(string periodKind) =>
        periodKind == BudgetNotice.MonthlyPeriod ? "в этом месяце" : "сегодня";

    private static string BuildText(BudgetPeriodStatus period) => period.State switch
    {
        BudgetState.Warn =>
            $"Бюджет LLM: предупреждение. Расход {PeriodLabel(period.Kind)}: ${period.Spend:0.00} из ${period.Limit:0.00} ({period.Percent}%).",
        BudgetState.Soft =>
            $"Бюджет LLM: достигнут порог 100%. Расход {PeriodLabel(period.Kind)}: ${period.Spend:0.00} из ${period.Limit:0.00}. " +
            "Включены ограничения: доступны только быстрые и бесплатные модели.",
        BudgetState.Hard =>
            $"Бюджет LLM исчерпан. Расход {PeriodLabel(period.Kind)}: ${period.Spend:0.00} из ${period.Limit:0.00}. " +
            "Доступны только бесплатные модели.",
        _ => string.Empty
    };

    private async Task<bool> TryInsertNoticeAsync(string periodKind, DateTimeOffset periodStart, int threshold, CancellationToken cancellationToken)
    {
        var notice = new BudgetNotice
        {
            PeriodKind = periodKind,
            PeriodStart = periodStart,
            Threshold = threshold,
            CreatedAt = _clock.UtcNow
        };
        _db.BudgetNotices.Add(notice);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.Entry(notice).State = EntityState.Detached;
            return false;
        }
    }

    private static bool IsUniqueViolation(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
