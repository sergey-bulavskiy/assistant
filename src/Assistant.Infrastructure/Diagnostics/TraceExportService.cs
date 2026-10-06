using System.Text.Json;
using System.Data;
using Assistant.Application.Diagnostics;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AssistantDbContext _db;
    private readonly TraceSecretRegistry? _secrets;

    public TraceExportService(AssistantDbContext db, TraceSecretRegistry? secrets = null)
    {
        _db = db;
        _secrets = secrets;
    }

    public async Task<TraceExportDocument?> ReadByMessageIdAsync(long messageId, CancellationToken ct) =>
        await ReadAsync(messageId, null, null, null, ct);

    public async Task<TraceExportDocument?> ReadByTelegramReferenceAsync(
        long botId, long chatId, int telegramMessageId, CancellationToken ct) =>
        await ReadAsync(null, botId, chatId, telegramMessageId, ct);

    private async Task<TraceExportDocument?> ReadAsync(long? messageId, long? botId,
        long? chatId, int? telegramMessageId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await _db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var message = messageId is { } id
            ? await _db.Messages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.Id == id).Select(m => new { m.Id, m.FamilyId, m.BotId, m.ChatId, m.TopicId }).SingleOrDefaultAsync(ct)
            : await _db.Messages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.BotId == botId && m.ChatId == chatId && m.TelegramMessageId == telegramMessageId)
                .Select(m => new { m.Id, m.FamilyId, m.BotId, m.ChatId, m.TopicId }).SingleOrDefaultAsync(ct);
        if (message is null) return null;

        var relatedIds = await _db.DebugTraceEvents.AsNoTracking()
            .Where(e => e.RelatedSourceMessageId == message.Id)
            .Select(e => e.TraceId).Distinct().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-TraceOptions.MaximumRetentionDays);
        var traces = await _db.DebugTraces.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.CreatedAt > cutoff && t.ExpiresAt > now
                && (t.SourceMessageId == message.Id || relatedIds.Contains(t.Id)))
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync(ct);
        if (traces.Count == 0) return null;

        var traceIds = traces.Select(t => t.Id).ToArray();
        var events = await _db.DebugTraceEvents.AsNoTracking()
            .Where(e => traceIds.Contains(e.TraceId))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        var callIds = events.Where(e => e.LlmCallId.HasValue).Select(e => e.LlmCallId!.Value)
            .Distinct().ToArray();
        var calls = await _db.LlmCalls.IgnoreQueryFilters().AsNoTracking()
            .Where(c => callIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var coverage = await _db.DebugTraceCoverage.AsNoTracking().SingleOrDefaultAsync(c => c.Id == 1, ct);
        var retained = _db.DebugTraces.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.CreatedAt > cutoff && t.ExpiresAt > now);
        var retainedCount = await retained.CountAsync(ct);
        var oldest = await retained.Select(t => (DateTimeOffset?)t.CreatedAt).MinAsync(ct);
        var newest = await retained.Select(t => (DateTimeOffset?)t.CreatedAt).MaxAsync(ct);

        // Stored incoming messages are evidence of admitted interactions even when diagnostics
        // were off or failed. Export only bounded timestamp metadata, never their content.
        // These observations cannot establish why capture was missing or the switch's history.
        var scopeStart = traces.Min(t => t.CreatedAt);
        var incoming = _db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Direction == MessageDirection.In && m.FamilyId == message.FamilyId
                && m.BotId == message.BotId && m.ChatId == message.ChatId && m.TopicId == message.TopicId);
        var originals = incoming.Where(m => m.CreatedAt >= scopeStart && m.CreatedAt <= now
            && !retained.Any(t => t.SourceMessageId == m.Id && !t.IsEdit))
            .Select(m => new { At = m.CreatedAt, Kind = "original" });
        var edits = incoming.Where(m => m.EditedAt >= scopeStart && m.EditedAt <= now
            && !retained.Any(t => t.SourceMessageId == m.Id && t.IsEdit && t.CreatedAt >= m.EditedAt))
            .Select(m => new { At = m.EditedAt!.Value, Kind = "latest_edit" });
        var missing = originals.Concat(edits);
        var missingCount = await missing.CountAsync(ct);
        var observed = await missing.OrderBy(m => m.At).ThenBy(m => m.Kind).Take(64).ToListAsync(ct);

        var eventGroups = events.GroupBy(e => e.TraceId).ToDictionary(g => g.Key, g => g.ToArray());
        var exported = traces.Select(t => new TraceExportItem(
            t.Id, t.CreatedAt, t.ExpiresAt, t.LastEventAt, t.FamilyId, t.BotId, t.ChatId, t.TopicId,
            t.UpdateId, t.SourceMessageId, t.IsEdit, t.Kind, t.BuildIdentity, t.SchemaVersion,
            t.PayloadBytes, t.AccountedBytes, t.Truncated, t.Redacted, t.OmittedEventCount, t.FinalOutcome,
            t.LastDisposition, t.CompletedAt,
            eventGroups.GetValueOrDefault(t.Id, []).Select(e => new TraceExportEvent(
                e.Id, e.CreatedAt, e.FamilyId, e.BotId, e.ChatId, e.TopicId,
                e.SourceMessageId, e.Stage, e.Outcome, e.ReasonCode,
                Label(e.Stage, e.Outcome, e.ReasonCode), e.AttemptId, e.LlmCallId,
                e.PendingRecordId, e.RelatedSourceMessageId, e.ActorId,
                JsonSerializer.Deserialize<TraceExportDetail>(e.DetailJson, JsonOptions) ?? new TraceExportDetail(),
                e.PayloadBytes, e.AccountedBytes)).ToArray())).ToArray();
        var summaries = callIds.Select(callId => calls.GetValueOrDefault(callId))
            .Where(c => c is not null).Cast<LlmCall>()
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new TraceExportLlmCall(c.Id, c.CreatedAt,
                _secrets?.Redact(c.Provider) ?? c.Provider,
                _secrets?.Redact(c.Model) ?? c.Model,
                _secrets?.Redact(c.Tier) ?? c.Tier,
                c.Outcome.ToString(), c.InputTokens, c.OutputTokens, c.Cost, c.DurationMs,
                _secrets is not null && (_secrets.Redact(c.Provider) != c.Provider
                    || _secrets.Redact(c.Model) != c.Model || _secrets.Redact(c.Tier) != c.Tier))).ToArray();
        var window = new TraceExportCoverage(
            oldest, newest, retainedCount, coverage?.RetainedBytes ?? 0, coverage?.EvictedCount ?? 0,
            coverage?.LastEvictedAt, scopeStart, now, missingCount,
            Math.Max(0, missingCount - observed.Count),
            observed.Select(m => new TraceExportGap(m.At, m.At, m.Kind, "capture_not_observed")).ToArray());
        await tx.CommitAsync(ct);
        return new TraceExportDocument(message.Id, DateTimeOffset.UtcNow, window,
            "Observed gaps are stored incoming interactions without a matching retained trace in this source's family/bot/chat/topic window. Their cause is unknown; they do not prove continuous disabled periods. Earlier edits, disabled periods without stored interactions, expiry, cap eviction, storage failures, callbacks and reactions may leave other gaps.",
            exported, summaries);
    }

    private static string Label(string stage, string outcome, string? reason) =>
        reason is null ? $"{stage}: {outcome.Replace('_', ' ')}" :
        $"{stage}: {outcome.Replace('_', ' ')} ({reason.Replace('_', ' ')})";
}

public sealed record TraceExportDocument(
    long SourceMessageId, DateTimeOffset ExportedAt, TraceExportCoverage Coverage,
    string CoverageNote, IReadOnlyList<TraceExportItem> Traces,
    IReadOnlyList<TraceExportLlmCall> LlmCalls);

public sealed record TraceExportCoverage(
    DateTimeOffset? OldestRetainedAt, DateTimeOffset? NewestRetainedAt,
    int RetainedTraceCount, long AccountedBytes, long CapEvictionCount,
    DateTimeOffset? LastCapEvictionAt, DateTimeOffset ObservedFrom, DateTimeOffset ObservedThrough,
    int ObservedMissingInteractionCount, int OmittedGapCount, IReadOnlyList<TraceExportGap> ObservedGaps);

public sealed record TraceExportGap(
    DateTimeOffset From, DateTimeOffset Through, string InteractionKind, string Evidence);

public sealed record TraceExportItem(
    Guid TraceId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset LastEventAt, long FamilyId,
    long BotId, long ChatId, int? TopicId, long? UpdateId, long? SourceMessageId,
    bool IsEdit, string Kind, string BuildIdentity, int SchemaVersion,
    long PayloadBytes, long AccountedBytes, bool Truncated, bool Redacted, int OmittedEventCount,
    string? FinalOutcome, string? LastDisposition, DateTimeOffset? CompletedAt,
    IReadOnlyList<TraceExportEvent> Events);

public sealed record TraceExportEvent(
    long EventId, DateTimeOffset At, long FamilyId, long BotId, long ChatId,
    int? TopicId, long? SourceMessageId, string Stage, string Outcome, string? ReasonCode,
    string Label, Guid? AttemptId, long? LlmCallId, long? PendingRecordId,
    long? RelatedSourceMessageId, long? ActorId, TraceExportDetail Detail,
    int PayloadBytes, int AccountedBytes);

public sealed record TraceExportDetail
{
    public string? Text { get; init; }
    public bool? Sent { get; init; }
    public IReadOnlyList<TraceModelMessage>? Messages { get; init; }
    public TraceModelOptions? Options { get; init; }
    public int? PartIndex { get; init; }
    public int? PartCount { get; init; }
    public int? TelegramMessageId { get; init; }
    public string? Operation { get; init; }
    public int? EventCount { get; init; }
    public int? ProblemCount { get; init; }
    public int? OmittedMessages { get; init; }
    public bool Truncated { get; init; }
    public bool Redacted { get; init; }
}

public sealed record TraceExportLlmCall(
    long Id, DateTimeOffset CreatedAt, string Provider, string Model, string Tier,
    string Outcome, int? InputTokens, int? OutputTokens, decimal Cost, long DurationMs,
    bool Redacted);
