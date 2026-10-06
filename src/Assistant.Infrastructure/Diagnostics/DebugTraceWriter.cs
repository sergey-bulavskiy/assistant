using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Domain.Diagnostics;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class DebugTraceWriter
{
    private const long AdvisoryLockKey = 0x4252414E54524345; // One lock across all processes for diagnostic admission.
    // Conservative fixed reserves exceed the largest serialized bounded metadata object.
    // The exact serialized JSON detail bytes are added separately to each event.
    private const int TraceOverheadBytes = 1024;
    private const int EventOverheadBytes = 1024;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly TraceOptions _options;
    private readonly TraceRedactor _redactor;
    private readonly TraceCaptureState _state;

    public DebugTraceWriter(IServiceScopeFactory scopeFactory, IClock clock, TraceOptions options,
        TraceRedactor redactor, TraceCaptureState state)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _redactor = redactor;
        _state = state;
    }

    public async Task<Guid?> StartAsync(TraceStart start, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _state.Paused) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TraceOptions.WriteTimeout);
        await using var scope = _scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(start.FamilyId);
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        db.Database.SetCommandTimeout(TraceOptions.WriteTimeout);
        await using var tx = await db.Database.BeginTransactionAsync(timeout.Token);
        await LockAsync(db, timeout.Token);
        var coverage = await CoverageAsync(db, timeout.Token);
        var now = _clock.UtcNow;
        Guid? existingId = null;
        var existingRetained = false;
        if (start.UpdateId is { } updateId)
        {
            var existing = await db.DebugTraces.IgnoreQueryFilters()
                .Where(t => t.BotId == start.BotId && t.UpdateId == updateId)
                .Select(t => new { t.Id, t.FamilyId, t.ExpiresAt, t.CreatedAt })
                .SingleOrDefaultAsync(timeout.Token);
            if (existing is not null)
            {
                existingId = existing.Id;
                existingRetained = existing.FamilyId == start.FamilyId && existing.ExpiresAt > now
                    && existing.CreatedAt > now.AddDays(-TraceOptions.MaximumRetentionDays);
            }
        }
        await PruneExpiredBatchAsync(db, coverage, now, timeout.Token);
        if (existingId is not null)
        {
            await db.SaveChangesAsync(timeout.Token);
            await tx.CommitAsync(timeout.Token);
            return existingRetained ? existingId : null;
        }

        var kind = _redactor.SanitizeMetadata(start.Kind, 32);
        var build = _redactor.SanitizeMetadata(start.BuildIdentity ?? "unknown", 64);
        var root = new DebugTrace
        {
            Id = start.TraceId,
            FamilyId = start.FamilyId,
            BotId = start.BotId,
            ChatId = start.ChatId,
            TopicId = start.TopicId,
            UpdateId = start.UpdateId,
            SourceMessageId = start.SourceMessageId,
            IsEdit = start.IsEdit,
            Kind = kind.Value,
            BuildIdentity = build.Value,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RetentionDays),
            LastEventAt = now,
            AccountedBytes = TraceOverheadBytes
        };

        var sourceData = new TraceEventData("source", "admitted", Text: start.SourceText);
        var prepared = _redactor.Prepare(sourceData, _options.MaxDetailBytes);
        var sourceEvent = NewEvent(root, now, prepared);
        root.PayloadBytes = prepared.PayloadBytes;
        root.AccountedBytes += sourceEvent.AccountedBytes;
        root.EventCount = 1;
        root.Truncated = prepared.Truncated || kind.Truncated || build.Truncated;
        root.Redacted = prepared.Redacted || kind.Redacted || build.Redacted;

        await MakeRoomAsync(db, coverage, root.AccountedBytes, null, now, timeout.Token);
        db.DebugTraces.Add(root);
        db.DebugTraceEvents.Add(sourceEvent);
        coverage.RetainedBytes += root.AccountedBytes;
        await db.SaveChangesAsync(timeout.Token);
        await tx.CommitAsync(timeout.Token);
        return root.Id;
    }

    public async Task AppendAsync(Guid traceId, long familyId, TraceEventData data, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _state.Paused) return;
        TraceRedactor.Validate(data);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TraceOptions.WriteTimeout);
        await using var scope = _scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(familyId);
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        db.Database.SetCommandTimeout(TraceOptions.WriteTimeout);
        await using var tx = await db.Database.BeginTransactionAsync(timeout.Token);
        await LockAsync(db, timeout.Token);
        var coverage = await CoverageAsync(db, timeout.Token);
        var now = _clock.UtcNow;
        await PruneExpiredBatchAsync(db, coverage, now, timeout.Token);
        var root = await db.DebugTraces.SingleOrDefaultAsync(t => t.Id == traceId && t.FamilyId == familyId, timeout.Token);
        if (root is null || root.ExpiresAt <= now || root.CreatedAt <= now.AddDays(-TraceOptions.MaximumRetentionDays))
        {
            await tx.CommitAsync(timeout.Token);
            return; // An expired or cap-evicted interaction is never recreated by a late event.
        }

        if (data.Stage == "interaction")
        {
            root.FinalOutcome = data.Outcome;
            if (data.Outcome is "completed" or "failed" or "cancelled" or "skipped") root.CompletedAt = now;
        }
        if (data.Stage is "answer" or "delivery" or "confirmation")
            root.LastDisposition = $"{data.Stage}:{data.Outcome}";

        if (root.EventCount >= _options.MaxEvents)
        {
            root.Truncated = true;
            if (root.OmittedEventCount < int.MaxValue) root.OmittedEventCount++;
            await db.SaveChangesAsync(timeout.Token);
            await tx.CommitAsync(timeout.Token);
            return;
        }

        var remaining = Math.Max(0, _options.MaxDetailBytes - (int)root.PayloadBytes);
        var prepared = _redactor.Prepare(data, remaining);
        // After the detail budget is spent, retain only a finite number of content-free
        // completion/transport markers. They still count against the global byte cap.
        var metadataOnly = prepared.DetailJson is null || remaining == 0;
        if (metadataOnly && data.Stage is not ("interaction" or "delivery" or "answer"
            or "confirmation" or "model_request" or "model_result"))
        {
            root.Truncated = true;
            if (root.OmittedEventCount < int.MaxValue) root.OmittedEventCount++;
            await db.SaveChangesAsync(timeout.Token);
            await tx.CommitAsync(timeout.Token);
            return;
        }

        var row = NewEvent(root, now, prepared);
        if (metadataOnly)
        {
            row.DetailJson = TraceRedactor.SerializeMetadataOnly(data, prepared.Redacted);
            row.PayloadBytes = 0;
            row.AccountedBytes = EventOverheadBytes + System.Text.Encoding.UTF8.GetByteCount(row.DetailJson);
        }
        if (!await MakeRoomAsync(db, coverage, row.AccountedBytes, root.Id, now, timeout.Token))
        {
            // The active root was itself oldest. Evict it before newer traces and drop this
            // late event; later appends will not recreate it.
            db.Entry(root).State = EntityState.Detached;
            await db.SaveChangesAsync(timeout.Token);
            await tx.CommitAsync(timeout.Token);
            return;
        }
        db.DebugTraceEvents.Add(row);
        root.PayloadBytes += row.PayloadBytes;
        root.AccountedBytes += row.AccountedBytes;
        root.EventCount++;
        root.LastEventAt = now;
        root.Truncated |= prepared.Truncated || metadataOnly;
        root.Redacted |= prepared.Redacted;
        coverage.RetainedBytes += row.AccountedBytes;
        await db.SaveChangesAsync(timeout.Token);
        await tx.CommitAsync(timeout.Token);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        // Each batch has its own transaction and deadline. A later timeout cannot undo
        // earlier expiry deletes, and a large backlog is drained over multiple commits.
        while (await CleanupBatchAsync(cancellationToken)) { }
        _state.Resume();
    }

    public async Task<bool> CleanupBatchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TraceOptions.WriteTimeout);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        db.Database.SetCommandTimeout(TraceOptions.WriteTimeout);
        await using var tx = await db.Database.BeginTransactionAsync(timeout.Token);
        await LockAsync(db, timeout.Token);
        var coverage = await CoverageAsync(db, timeout.Token);
        var now = _clock.UtcNow;
        var deleted = await PruneExpiredBatchAsync(db, coverage, now, timeout.Token);
        if (deleted == 0)
            await MakeRoomAsync(db, coverage, 0, null, now, timeout.Token);
        await db.SaveChangesAsync(timeout.Token);
        await tx.CommitAsync(timeout.Token);
        return deleted > 0;
    }

    private static async Task LockAsync(AssistantDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
    }

    private static async Task<DebugTraceCoverage> CoverageAsync(AssistantDbContext db, CancellationToken ct)
    {
        var coverage = await db.DebugTraceCoverage.SingleOrDefaultAsync(c => c.Id == 1, ct);
        if (coverage is not null) return coverage;
        coverage = new DebugTraceCoverage { Id = 1 };
        db.DebugTraceCoverage.Add(coverage);
        return coverage;
    }

    private async Task<long> PruneExpiredBatchAsync(AssistantDbContext db, DebugTraceCoverage coverage,
        DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-TraceOptions.MaximumRetentionDays);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = (int)TraceOptions.WriteTimeout.TotalSeconds;
        command.CommandText = """
            WITH victims AS (
                SELECT id FROM debug_traces
                WHERE expires_at <= @now OR created_at <= @cutoff
                ORDER BY created_at, id
                LIMIT 100
            ), deleted AS (
                DELETE FROM debug_traces AS t USING victims AS v
                WHERE t.id = v.id
                RETURNING t.accounted_bytes
            )
            SELECT count(*)::bigint, COALESCE(sum(accounted_bytes), 0)::bigint FROM deleted
            """;
        AddParameter(command, "now", now);
        AddParameter(command, "cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var count = reader.GetInt64(0);
        coverage.RetainedBytes = Math.Max(0, coverage.RetainedBytes - reader.GetInt64(1));
        return count;
    }

    private async Task<bool> MakeRoomAsync(AssistantDbContext db, DebugTraceCoverage coverage, long incoming,
        Guid? protectedTrace, DateTimeOffset now, CancellationToken ct)
    {
        var needed = coverage.RetainedBytes + incoming - _options.MaxStorageBytes;
        if (needed <= 0) return true;

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = (int)TraceOptions.WriteTimeout.TotalSeconds;
        command.CommandText = """
            WITH ranked AS (
                SELECT id, accounted_bytes,
                    COALESCE(sum(accounted_bytes) OVER (
                        ORDER BY created_at, id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
                    ), 0) AS bytes_before
                FROM debug_traces
            ), protected_boundary AS (
                SELECT bytes_before FROM ranked WHERE id = @protected
            ), deleted AS (
                DELETE FROM debug_traces AS t USING ranked AS r
                WHERE t.id = r.id AND r.bytes_before < @needed
                  AND (NOT EXISTS (SELECT 1 FROM protected_boundary)
                       OR r.bytes_before <= (SELECT bytes_before FROM protected_boundary))
                RETURNING t.id, t.accounted_bytes
            )
            SELECT count(*)::bigint, COALESCE(sum(accounted_bytes), 0)::bigint,
                   COALESCE(bool_or(id = @protected), false)
            FROM deleted
            """;
        AddParameter(command, "needed", needed);
        AddParameter(command, "protected", protectedTrace ?? Guid.Empty);
        bool protectedDeleted;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            var count = reader.GetInt64(0);
            coverage.RetainedBytes = Math.Max(0, coverage.RetainedBytes - reader.GetInt64(1));
            coverage.EvictedCount += count;
            if (count > 0) coverage.LastEvictedAt = now;
            protectedDeleted = reader.GetBoolean(2);
        }
        if (protectedDeleted)
        {
            // The append is now discarded. Enforce any pre-existing cap excess without
            // evicting newer traces to make room for content that will never be stored.
            if (coverage.RetainedBytes > _options.MaxStorageBytes)
                await MakeRoomAsync(db, coverage, 0, null, now, ct);
            return false;
        }
        if (coverage.RetainedBytes + incoming > _options.MaxStorageBytes)
            throw new InvalidOperationException("Trace retention could not make room.");
        return true;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private DebugTraceEvent NewEvent(DebugTrace root, DateTimeOffset now, TraceRedactor.PreparedTraceEvent prepared)
    {
        var data = prepared.Data;
        return new DebugTraceEvent
        {
            TraceId = root.Id,
            FamilyId = root.FamilyId,
            BotId = root.BotId,
            ChatId = root.ChatId,
            TopicId = root.TopicId,
            SourceMessageId = root.SourceMessageId,
            CreatedAt = now,
            Stage = data.Stage,
            Outcome = data.Outcome,
            ReasonCode = data.ReasonCode,
            AttemptId = data.AttemptId,
            LlmCallId = data.LlmCallId,
            PendingRecordId = data.PendingRecordId,
            RelatedSourceMessageId = data.RelatedSourceMessageId,
            ActorId = data.ActorId,
            DetailJson = _redactor.Serialize(prepared),
            PayloadBytes = prepared.PayloadBytes,
            AccountedBytes = EventOverheadBytes + prepared.PayloadBytes
        };
    }

}
