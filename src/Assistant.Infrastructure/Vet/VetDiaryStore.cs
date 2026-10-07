using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

/// <summary>Dedicated scoped Vet persistence. Transactions serialize per bot; immutable evidence
/// is retained independently of mutable processing state. No message text or values are logged.</summary>
public sealed class VetDiaryStore(AssistantDbContext db, ICurrentFamily current, IClock clock) : IVetDiaryStore
{
    private readonly VetStoreGuard _guard = new(db, current);
    private IQueryable<VetTextSource> Sources(VetDiaryScope s) => db.Set<VetTextSource>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.TelegramBotId == s.TelegramBotId
        && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private IQueryable<VetEvent> Events(VetDiaryScope s) => db.Set<VetEvent>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.TelegramBotId == s.TelegramBotId
        && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private IQueryable<VetPendingDecision> Pending(VetDiaryScope s) => db.Set<VetPendingDecision>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.TelegramBotId == s.TelegramBotId
        && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private IQueryable<VetDiaryAction> Actions(VetDiaryScope s) => db.Set<VetDiaryAction>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.TelegramBotId == s.TelegramBotId
        && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private Task CheckAsync(VetDiaryScope s, CancellationToken ct) =>
        _guard.BotAsync(s.FamilyId, s.BotDbId, s.TelegramBotId, ct);

    public async Task<VetAdmittedSource> AdmitAsync(VetDiaryScope scope, IncomingMessage message, long updateId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (message.UserId is not { } author || message.Text is not { Length: > 0 and <= 16000 } text)
            throw new InvalidOperationException("Vet source is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        var source = await Sources(scope).AsNoTracking().SingleOrDefaultAsync(s =>
            s.TelegramMessageId == message.MessageId && s.SourceSlot == 0, ct);
        var known = await db.Set<VetTextSourceRevision>().AsNoTracking().SingleOrDefaultAsync(r =>
            r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId && r.UpdateId == updateId, ct);
        if (known is not null)
        {
            if (source is null || known.SourceId != source.Id) throw new InvalidOperationException("Vet admission key conflict.");
            return new(source, known);
        }
        var hash = Hash(text);
        if (source is not null)
        {
            if (source.SourceAuthorUserId != author) throw new InvalidOperationException("Vet source attribution conflict.");
            var old = await db.Set<VetTextSourceRevision>().AsNoTracking().SingleAsync(r => r.Id == source.CurrentInputRevisionId, ct);
            if (old.ContentHash == hash || !message.IsEdit) return new(source, old);
            // Out-of-order edits never supersede a more recent immutable input.
            if (message.EditedAt is { } edited && old.EditedAt is { } previous && edited <= previous)
                return new(source, old);
            db.Attach(source);
            await Pending(scope).Where(p => p.SourceId == source.Id && p.State == "pending")
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.State, "superseded"), ct);
        }
        else
        {
            source = new VetTextSource
            {
                Id = Guid.NewGuid(), FamilyId = scope.FamilyId, BotDbId = scope.BotDbId,
                TelegramBotId = scope.TelegramBotId, ChatId = scope.ChatId, TopicId = scope.TopicId,
                ChatType = message.ChatType, TelegramMessageId = message.MessageId, SourceSlot = 0,
                SourceAuthorUserId = author, SentAt = message.SentAt,
                ReplyToMessageId = message.ReplyToMessageId, ReplyToUserId = message.ReplyToUserId
            };
            db.Add(source);
        }
        var revision = new VetTextSourceRevision
        {
            Id = Guid.NewGuid(), SourceId = source.Id, FamilyId = scope.FamilyId, BotDbId = scope.BotDbId,
            Ordinal = source.CurrentOrdinal + 1, Text = text, ContentHash = hash, UpdateId = updateId,
            AdmittedAt = clock.UtcNow, EditedAt = message.EditedAt, IsEdit = message.IsEdit,
            OperationKey = Guid.NewGuid()
        };
        source.CurrentInputRevisionId = revision.Id;
        source.CurrentOrdinal = revision.Ordinal;
        db.Add(revision);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.Entry(source).State = EntityState.Detached;
        db.Entry(revision).State = EntityState.Detached;
        return new(source, revision);
    }

    public async Task<VetAdmittedSource?> GetSourceAsync(VetDiaryScope scope, Guid sourceId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var source = await Sources(scope).AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, ct);
        return source is null ? null : new(source,
            await db.Set<VetTextSourceRevision>().AsNoTracking().SingleAsync(x => x.Id == source.CurrentInputRevisionId, ct));
    }

    public async Task<VetAdmittedSource?> FindSourceAsync(VetDiaryScope scope, int telegramMessageId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var sourceId = await Sources(scope).Where(s => s.TelegramMessageId == telegramMessageId && s.SourceSlot == 0)
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
        return sourceId is null ? null : await GetSourceAsync(scope, sourceId.Value, ct);
    }

    public async Task<IReadOnlyList<VetAdmittedSource>> GetResumableAsync(long familyId, long botDbId, int limit, CancellationToken ct)
    {
        await _guard.BotAsync(familyId, botDbId, null, ct);
        var sources = await db.Set<VetTextSource>().AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.BotDbId == botDbId
                && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == s.SourceAuthorUserId
                    && m.Status == Assistant.Domain.Families.FamilyMemberStatus.Approved)
                && (s.ChatType == "private" || db.Places.Any(p => p.BotId == botDbId && p.ChatId == s.ChatId
                    && p.TopicId == s.TopicId && p.Status == Assistant.Domain.Places.PlaceStatus.Approved))
                && (s.SourceMessageDbId != null || db.Messages.Any(m => m.FamilyId == familyId
                    && m.BotId == s.TelegramBotId && m.ChatId == s.ChatId && m.TopicId == s.TopicId
                    && m.TelegramMessageId == s.TelegramMessageId))
                && db.Set<VetTextSourceRevision>().Any(r => r.Id == s.CurrentInputRevisionId
                    && (r.State == "admitted" || r.State == "ready" || r.State == "written" || r.State == "dispatching")))
            .OrderBy(s => s.SentAt).ThenBy(s => s.Id).Take(Math.Clamp(limit, 1, 20)).ToListAsync(ct);
        var result = new List<VetAdmittedSource>();
        foreach (var source in sources)
        {
            // A pre-offset admission has no provider call until its transport row exists.
            if (source.SourceMessageDbId is null)
            {
                var messageId = await db.Messages.Where(m => m.FamilyId == familyId
                    && m.BotId == source.TelegramBotId && m.ChatId == source.ChatId
                    && m.TopicId == source.TopicId && m.TelegramMessageId == source.TelegramMessageId)
                    .Select(m => (long?)m.Id).SingleOrDefaultAsync(ct);
                if (messageId is null) continue;
                await LinkMessageAsync(new(familyId, botDbId, source.TelegramBotId, source.ChatId, source.TopicId),
                    source.Id, messageId.Value, ct);
                source.SourceMessageDbId = messageId;
            }
            result.Add(new(source, await db.Set<VetTextSourceRevision>().AsNoTracking()
                .SingleAsync(r => r.Id == source.CurrentInputRevisionId, ct)));
        }
        return result;
    }

    public async Task LinkMessageAsync(VetDiaryScope scope, Guid sourceId, long messageDbId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await db.Messages.AnyAsync(m => m.Id == messageDbId && m.FamilyId == scope.FamilyId
            && m.BotId == scope.TelegramBotId && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId
            && Sources(scope).Any(s => s.Id == sourceId && s.TelegramMessageId == m.TelegramMessageId), ct))
            throw new InvalidOperationException("Vet message linkage is invalid.");
        await Sources(scope).Where(s => s.Id == sourceId && (s.SourceMessageDbId == null || s.SourceMessageDbId == messageDbId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMessageDbId, messageDbId), ct);
    }

    private IQueryable<VetTextSourceRevision> CurrentRevision(VetDiaryScope scope, Guid id) =>
        db.Set<VetTextSourceRevision>().Where(r => r.Id == id && r.FamilyId == scope.FamilyId
            && r.BotDbId == scope.BotDbId && Sources(scope).Any(s => s.Id == r.SourceId && s.CurrentInputRevisionId == id));

    public async Task<bool> SetProcessingAsync(VetDiaryScope scope, Guid revisionId, string expectedState,
        string state, string? failureCategory, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await CurrentRevision(scope, revisionId).Where(r => r.State == expectedState)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, state)
                .SetProperty(r => r.FailureCategory, failureCategory), ct) == 1;
    }

    public async Task<bool> RetryAsync(VetDiaryScope scope, Guid sourceId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await db.Set<VetTextSourceRevision>().Where(r => r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId
            && Sources(scope).Any(s => s.Id == sourceId && s.CurrentInputRevisionId == r.Id)
            && (r.State == "paused" || r.State == "failed") && r.ExplicitRetryCount < 3)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, r => r.ExtractionResultId == null ? "admitted" : "ready")
                .SetProperty(r => r.ExplicitRetryCount, r => r.ExplicitRetryCount + 1)
                .SetProperty(r => r.FailureCategory, (string?)null), ct) == 1;
    }

    public async Task<VetExtractionResult?> GetResultAsync(VetDiaryScope scope, Guid revisionId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await db.Set<VetExtractionResult>().AsNoTracking().SingleOrDefaultAsync(r =>
            r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId && r.InputRevisionId == revisionId
            && CurrentRevision(scope, revisionId).Any(), ct);
    }

    public async Task<bool> SaveResultAsync(VetDiaryScope scope, Guid revisionId, string json, string modelName,
        Guid? attemptId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        var revision = await CurrentRevision(scope, revisionId).AsNoTracking().SingleOrDefaultAsync(ct);
        if (revision is null || revision.State != "dispatching" || revision.ExtractionResultId is not null) return false;
        var result = new VetExtractionResult
        {
            Id = Guid.NewGuid(), FamilyId = scope.FamilyId, BotDbId = scope.BotDbId,
            InputRevisionId = revisionId, Json = json, ModelName = modelName,
            AttemptId = attemptId, CreatedAt = clock.UtcNow
        };
        db.Add(result);
        await db.SaveChangesAsync(ct);
        await CurrentRevision(scope, revisionId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.ExtractionResultId, result.Id).SetProperty(r => r.AttemptId, attemptId)
            .SetProperty(r => r.State, "ready"), ct);
        await tx.CommitAsync(ct);
        db.Entry(result).State = EntityState.Detached;
        return true;
    }

    public async Task SetAnswerAsync(VetDiaryScope scope, Guid revisionId, string state, string? text, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await CurrentRevision(scope, revisionId).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.AnswerState, state).SetProperty(r => r.AnswerText, text), ct);
    }

    public async Task SaveWorkAsync(VetDiaryScope scope, Guid revisionId, string json, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await CurrentRevision(scope, revisionId).Where(r => r.WorkJson == null)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.WorkJson, json), ct);
    }

    public async Task SetHistoryAsync(VetDiaryScope scope, Guid revisionId, string json, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await CurrentRevision(scope, revisionId).ExecuteUpdateAsync(u => u.SetProperty(r => r.HistoryJson, json), ct);
    }

    public async Task<string?> GetLastHistoryAsync(VetDiaryScope scope, Guid exceptRevisionId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await db.Set<VetTextSourceRevision>().Where(r => r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId
                && r.Id != exceptRevisionId && r.HistoryJson != null
                && Sources(scope).Any(s => s.Id == r.SourceId && s.CurrentInputRevisionId == r.Id))
            .OrderByDescending(r => r.AdmittedAt).ThenByDescending(r => r.Ordinal)
            .Select(r => r.HistoryJson).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> GetReplyToAllAsync(VetDiaryScope scope, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await db.Places.Where(p => p.BotId == scope.BotDbId && p.ChatId == scope.ChatId
            && p.TopicId == scope.TopicId && p.Status == Assistant.Domain.Places.PlaceStatus.Approved)
            .Select(p => p.ReplyToAll).SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<VetEvent>> GetSourceEventsAsync(VetDiaryScope scope, Guid sourceId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await Events(scope).AsNoTracking().Where(e => e.SourceKind == "text" && e.SourceId == sourceId)
            .OrderBy(e => e.EventType).ThenBy(e => e.CandidateOrdinal).ToListAsync(ct);
    }

    public async Task<VetEvent?> GetEventAsync(VetDiaryScope scope, long eventId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
    }

    public async Task<IReadOnlyList<VetEvent>> FindDateTypeAsync(VetDiaryScope scope, string eventType,
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await Events(scope).AsNoTracking().Where(e => e.EventType == eventType && e.DeletedAt == null
            && e.OccurredAt >= from && e.OccurredAt < until).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Take(2).ToListAsync(ct);
    }

    public async Task<VetHistoryPage> QueryAsync(VetDiaryScope scope, long profileId, DateTimeOffset from,
        DateTimeOffset until, int offset, int pageSize, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (until <= from || offset < 0 || pageSize is < 1 or > 200
            || !await db.Set<VetProfile>().AnyAsync(p => p.Id == profileId && p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct))
            throw new InvalidOperationException("Vet history selection is invalid.");
        var query = db.Set<VetEvent>().AsNoTracking().Where(e => e.FamilyId == scope.FamilyId
            && e.BotDbId == scope.BotDbId && e.ProfileId == profileId && e.DeletedAt == null
            && e.OccurredAt >= from && e.OccurredAt < until);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Skip(offset).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(e => new VetHistoryFact(e.ChatId == scope.ChatId && e.TopicId == scope.TopicId ? e.Id : null,
            e.EventType, e.Value, e.Unit, e.Product, e.OccurredAt, e.Revision, e.Revision > 1)).ToArray(),
            total, offset, offset + rows.Count < total);
    }

    public async Task<VetPendingDecision> PutPendingAsync(VetDiaryScope scope, Guid sourceId, Guid revisionId,
        Guid resultId, long requester, string proposalJson, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await Sources(scope).AnyAsync(s => s.Id == sourceId && s.CurrentInputRevisionId == revisionId, ct))
            throw new InvalidOperationException("Vet source revision is stale.");
        var existing = await Pending(scope).AsNoTracking().SingleOrDefaultAsync(p => p.InputRevisionId == revisionId, ct);
        if (existing is not null) return existing;
        var decision = new VetPendingDecision
        {
            FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, TelegramBotId = scope.TelegramBotId,
            ChatId = scope.ChatId, TopicId = scope.TopicId, SourceId = sourceId, InputRevisionId = revisionId,
            ExtractionResultId = resultId, RequesterUserId = requester, ProposalJson = proposalJson,
            OperationKey = Guid.NewGuid(), CreatedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddHours(24)
        };
        db.Add(decision);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.Entry(decision).State = EntityState.Detached;
        return decision;
    }

    public async Task<IReadOnlyList<VetPendingDecision>> GetPendingAsync(VetDiaryScope scope, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await Pending(scope).Where(p => p.State == "pending" && p.ExpiresAt <= clock.UtcNow)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.State, "expired"), ct);
        return await Pending(scope).AsNoTracking().Where(p => p.State == "pending" && p.ExpiresAt > clock.UtcNow)
            .OrderBy(p => p.Id).ToListAsync(ct);
    }

    public async Task<VetPendingDecision?> GetPendingAsync(VetDiaryScope scope, long id, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return await Pending(scope).AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<bool> RevisePendingAsync(VetDiaryScope scope, long id, int expectedRevision, string proposalJson, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        var revised = await Pending(scope).Where(p => p.Id == id && p.State == "pending"
            && p.ExpiresAt > clock.UtcNow && p.ReviewRevision == expectedRevision
            && Sources(scope).Any(s => s.Id == p.SourceId && s.CurrentInputRevisionId == p.InputRevisionId))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ProposalJson, proposalJson)
                .SetProperty(p => p.ReviewRevision, p => p.ReviewRevision + 1)
                .SetProperty(p => p.OperationKey, Guid.NewGuid()).SetProperty(p => p.PromptMessageId, (int?)null), ct) == 1;
        await tx.CommitAsync(ct);
        return revised;
    }

    public async Task SetPromptAsync(VetDiaryScope scope, long id, int reviewRevision, int messageId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await Pending(scope).Where(p => p.Id == id && p.State == "pending" && p.ReviewRevision == reviewRevision)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.PromptMessageId, messageId), ct);
    }

    public async Task<bool> DeclineAsync(VetDiaryScope scope, long id, int reviewRevision, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await _guard.ActorAsync(scope, actorUserId, ct)) return false;
        var declined = await Pending(scope).Where(p => p.Id == id && p.State == "pending"
            && p.ReviewRevision == reviewRevision && p.ExpiresAt > clock.UtcNow
            && Sources(scope).Any(s => s.Id == p.SourceId && s.CurrentInputRevisionId == p.InputRevisionId))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.State, "declined")
                .SetProperty(p => p.ResolvedByUserId, actorUserId), ct) == 1;
        await tx.CommitAsync(ct);
        return declined;
    }

    public async Task<VetMutationResult> ApplyAsync(VetDiaryMutation mutation, CancellationToken ct)
    {
        var scope = mutation.Scope;
        await CheckAsync(scope, ct);
        using var tracking = new VetMutationTracking(db);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await _guard.ActorAsync(scope, mutation.ActorUserId, ct)) return VetMutationResult.Of(VetMutationStatus.Refused);
        var fingerprint = Hash(JsonSerializer.Serialize(mutation));
        var prior = await db.Set<VetDiaryAction>().AsNoTracking().SingleOrDefaultAsync(a =>
            a.FamilyId == scope.FamilyId && a.BotDbId == scope.BotDbId && a.OperationKey == mutation.OperationKey, ct);
        if (prior is not null)
            return prior.Fingerprint == fingerprint
                ? JsonSerializer.Deserialize<VetMutationResult>(prior.OutcomeJson)! with { Status = VetMutationStatus.AlreadyApplied }
                : VetMutationResult.Of(VetMutationStatus.Refused);
        if (mutation.OperationKey == Guid.Empty || mutation.Changes.Count is < 1 or > 200
            || !await db.Set<VetProfile>().AnyAsync(p => p.Id == mutation.ProfileId && p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct))
            return VetMutationResult.Of(VetMutationStatus.Refused);
        if (mutation.SourceId is { } sourceId && !await Sources(scope).AnyAsync(s =>
            s.Id == sourceId && s.CurrentInputRevisionId == mutation.InputRevisionId, ct))
            return VetMutationResult.Of(VetMutationStatus.Stale);
        VetPendingDecision? pending = null;
        if (mutation.PendingDecisionId is { } pendingId)
        {
            pending = await Pending(scope).AsNoTracking().SingleOrDefaultAsync(p => p.Id == pendingId, ct);
            if (pending is null) return VetMutationResult.Of(VetMutationStatus.NotFound);
            if (pending.State != "pending" || pending.ReviewRevision != mutation.ReviewRevision || pending.ExpiresAt <= clock.UtcNow
                || pending.InputRevisionId != mutation.InputRevisionId || pending.SourceId != mutation.SourceId)
                return VetMutationResult.Of(VetMutationStatus.Stale);
            var reviewed = JsonSerializer.Deserialize<VetProposal>(pending.ProposalJson);
            if (reviewed is null || reviewed.RequiresTargetSelection || reviewed.RequiresClarification
                || JsonSerializer.Serialize(reviewed.Changes) != JsonSerializer.Serialize(mutation.Changes))
                return VetMutationResult.Of(VetMutationStatus.Refused);
        }
        var affected = new List<(VetEvent Event, VetEventState? Before, int? BeforeRevision, VetEventState After)>();
        var usedIds = new HashSet<long>();
        var usedCandidates = new HashSet<(string, Guid, string, int)>();
        foreach (var change in mutation.Changes)
        {
            if (!ValidState(change.State) || !usedCandidates.Add((change.State.SourceKind, change.State.SourceId,
                change.State.EventType, change.State.CandidateOrdinal)))
                return VetMutationResult.Of(VetMutationStatus.Refused);
            if (!await Sources(scope).AnyAsync(s => s.Id == change.State.SourceId
                    && s.SourceAuthorUserId == change.State.SourceAuthorUserId
                    && s.SourceMessageDbId == change.State.SourceMessageDbId
                    && s.TelegramMessageId == change.State.TelegramMessageId, ct)
                || !await db.Set<VetExtractionResult>().AnyAsync(r => r.Id == change.State.ExtractionResultId
                    && r.InputRevisionId == change.State.InputRevisionId && r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId, ct))
                return VetMutationResult.Of(VetMutationStatus.Refused);
            VetEvent row;
            if (change.State.DeletedAt is null && !await Sources(scope)
                .AnyAsync(s => s.CurrentInputRevisionId == change.State.InputRevisionId, ct))
                return VetMutationResult.Of(VetMutationStatus.Stale);
            VetEventState? before = null;
            int? beforeRevision = null;
            if (change.EventId is { } eventId)
            {
                if (!usedIds.Add(eventId)) return VetMutationResult.Of(VetMutationStatus.Refused);
                var found = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, ct);
                if (found is null) return VetMutationResult.Of(VetMutationStatus.NotFound);
                row = found;
                if (row.Revision != change.ExpectedRevision) return VetMutationResult.Of(VetMutationStatus.Stale);
                before = State(row);
                beforeRevision = row.Revision;
                if (before.SourceId != change.State.SourceId || before.SourceKind != change.State.SourceKind
                    || before.CandidateOrdinal != change.State.CandidateOrdinal || before.EventType != change.State.EventType
                    || before.SourceAuthorUserId != change.State.SourceAuthorUserId
                    || before.SourceMessageDbId != change.State.SourceMessageDbId
                    || before.TelegramMessageId != change.State.TelegramMessageId)
                    return VetMutationResult.Of(VetMutationStatus.Refused);
                if (SameFact(before, change.State)) continue;
            }
            else
            {
                if (change.ExpectedRevision is not null || change.State.DeletedAt is not null
                    || await Events(scope).AnyAsync(e => e.SourceKind == change.State.SourceKind && e.SourceId == change.State.SourceId
                        && e.EventType == change.State.EventType && e.CandidateOrdinal == change.State.CandidateOrdinal, ct))
                    return VetMutationResult.Of(VetMutationStatus.Stale);
                row = new VetEvent
                {
                    FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, TelegramBotId = scope.TelegramBotId,
                    ChatId = scope.ChatId, TopicId = scope.TopicId, ProfileId = mutation.ProfileId, CreatedAt = clock.UtcNow
                };
            }
            affected.Add((row, before, beforeRevision, change.State));
        }
        if (affected.Count == 0)
        {
            if (pending is not null)
                await ResolvePendingAsync(scope, pending.Id, mutation.ActorUserId, ct);
            await tx.CommitAsync(ct);
            return VetMutationResult.Of(VetMutationStatus.NoChange);
        }
        var action = NewAction(scope, mutation.OperationKey, mutation.ActorUserId, mutation.Kind, fingerprint);
        action.SourceId = mutation.SourceId;
        action.PendingDecisionId = mutation.PendingDecisionId;
        db.Add(action);
        foreach (var item in affected)
        {
            if (item.Event.Id == 0) db.Add(item.Event); else db.Attach(item.Event);
            ApplyState(item.Event, item.After);
            item.Event.Revision = item.BeforeRevision is { } revision ? checked(revision + 1) : 1;
            item.Event.UpdatedAt = clock.UtcNow;
            item.Event.LastMutationKind = mutation.Kind;
        }
        await db.SaveChangesAsync(ct);
        foreach (var item in affected) db.Add(new VetDiaryActionChange
        {
            FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, ActionId = action.Id, EventId = item.Event.Id,
            BeforeJson = item.Before is null ? null : JsonSerializer.Serialize(item.Before),
            AfterJson = JsonSerializer.Serialize(item.After), BeforeRevision = item.BeforeRevision,
            AfterRevision = item.Event.Revision
        });
        var outcome = new VetMutationResult(VetMutationStatus.Applied, action.Id, affected.Select(x => x.Event.Id).ToArray(), [])
        { Revisions = affected.Select(x => new VetEventRevision(x.Event.Id, x.Event.Revision)).ToArray() };
        action.OutcomeJson = JsonSerializer.Serialize(outcome);
        if (pending is not null) await ResolvePendingAsync(scope, pending.Id, mutation.ActorUserId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var item in affected) db.Entry(item.Event).State = EntityState.Detached;
        db.Entry(action).State = EntityState.Detached;
        return outcome;
    }

    public async Task<VetMutationResult> UndoAsync(VetDiaryScope scope, long actorUserId, Guid operationKey, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        using var tracking = new VetMutationTracking(db);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await _guard.ActorAsync(scope, actorUserId, ct)) return VetMutationResult.Of(VetMutationStatus.Refused);
        var fingerprint = Hash(JsonSerializer.Serialize(new { scope, actorUserId, Kind = "undo" }));
        var replay = await db.Set<VetDiaryAction>().AsNoTracking().SingleOrDefaultAsync(a =>
            a.FamilyId == scope.FamilyId && a.BotDbId == scope.BotDbId && a.OperationKey == operationKey, ct);
        if (replay is not null) return replay.Fingerprint == fingerprint
            ? JsonSerializer.Deserialize<VetMutationResult>(replay.OutcomeJson)! with { Status = VetMutationStatus.AlreadyApplied }
            : VetMutationResult.Of(VetMutationStatus.Refused);
        var target = await Actions(scope).AsNoTracking().Where(a => a.ActorUserId == actorUserId
            && a.Kind != "undo" && a.ReversedByActionId == null && a.CreatedAt >= clock.UtcNow.AddHours(-24))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (target is null) return VetMutationResult.Of(VetMutationStatus.NotFound);
        var changes = await db.Set<VetDiaryActionChange>().AsNoTracking().Where(c =>
            c.FamilyId == scope.FamilyId && c.BotDbId == scope.BotDbId && c.ActionId == target.Id).OrderBy(c => c.Id).ToListAsync(ct);
        var reversal = NewAction(scope, operationKey, actorUserId, "undo", fingerprint);
        reversal.ReversesActionId = target.Id;
        var applied = new List<long>();
        var protectedIds = new List<long>();
        var inverses = new List<(VetEvent Row, VetEventState Before, int Revision, VetEventState After)>();
        foreach (var change in changes)
        {
            var row = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == change.EventId, ct);
            if (row is null || row.Revision != change.AfterRevision) { protectedIds.Add(change.EventId); continue; }
            var before = State(row);
            var inverse = change.BeforeJson is null
                ? before with { DeletedAt = clock.UtcNow, DeleteReason = "undo", DeletedByUserId = actorUserId }
                : JsonSerializer.Deserialize<VetEventState>(change.BeforeJson)!;
            inverses.Add((row, before, row.Revision, inverse));
        }
        db.Add(reversal);
        db.Attach(target);
        foreach (var item in inverses)
        {
            db.Attach(item.Row);
            ApplyState(item.Row, item.After);
            item.Row.Revision++;
            item.Row.UpdatedAt = clock.UtcNow;
            item.Row.LastMutationKind = "undo";
            applied.Add(item.Row.Id);
        }
        await db.SaveChangesAsync(ct);
        target.ReversedByActionId = reversal.Id;
        foreach (var item in inverses) db.Add(new VetDiaryActionChange
        {
            FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, ActionId = reversal.Id, EventId = item.Row.Id,
            BeforeJson = JsonSerializer.Serialize(item.Before), AfterJson = JsonSerializer.Serialize(item.After),
            BeforeRevision = item.Revision, AfterRevision = item.Row.Revision
        });
        var result = new VetMutationResult(VetMutationStatus.Applied, reversal.Id, applied, protectedIds)
        { Revisions = inverses.Select(x => new VetEventRevision(x.Row.Id, x.Row.Revision)).ToArray() };
        reversal.OutcomeJson = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var item in inverses) db.Entry(item.Row).State = EntityState.Detached;
        db.Entry(target).State = EntityState.Detached;
        db.Entry(reversal).State = EntityState.Detached;
        return result;
    }

    private Task<int> ResolvePendingAsync(VetDiaryScope scope, long id, long actor, CancellationToken ct) =>
        Pending(scope).Where(p => p.Id == id).ExecuteUpdateAsync(u =>
            u.SetProperty(p => p.State, "accepted").SetProperty(p => p.ResolvedByUserId, actor), ct);

    private VetDiaryAction NewAction(VetDiaryScope scope, Guid key, long actor, string kind, string fingerprint) => new()
    {
        FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, TelegramBotId = scope.TelegramBotId,
        ChatId = scope.ChatId, TopicId = scope.TopicId, OperationKey = key, ActorUserId = actor,
        Kind = kind, Fingerprint = fingerprint, OutcomeJson = "{}", CreatedAt = clock.UtcNow
    };

    private static bool ValidState(VetEventState s) => s.Value > 0 && s.CandidateOrdinal >= 0
        && (s.EventType == "glucose" && s.Unit == "mmol/L" || s.EventType == "insulin" && s.Unit == "U")
        && s.Product?.Length is not > 100 && s.SourceKind == "text" && s.TextSourceId == s.SourceId
        && s.SourceAuthorUserId > 0 && s.SourceMessageDbId > 0;

    public static VetEventState State(VetEvent e) => new(e.EventType, e.Value, e.Unit, e.Product,
        e.OccurredAt, e.LocalTime, e.TimeZoneSnapshot, e.OccurredAtSource, e.ValueUnitSource,
        e.SourceKind, e.SourceId, e.CandidateOrdinal, e.TextSourceId, e.PhotoSourceId, e.PhotoBatchId,
        e.InputRevisionId, e.ExtractionResultId, e.SourceAuthorUserId, e.SourceMessageDbId,
        e.TelegramMessageId, e.DeletedAt, e.DeleteReason, e.DeletedByUserId);

    private static bool SameFact(VetEventState a, VetEventState b) =>
        a.EventType == b.EventType && a.Value == b.Value && a.Unit == b.Unit && a.Product == b.Product
        && a.OccurredAt == b.OccurredAt && a.DeletedAt == b.DeletedAt;

    private static void ApplyState(VetEvent e, VetEventState s)
    {
        e.EventType = s.EventType; e.Value = s.Value; e.Unit = s.Unit; e.Product = s.Product;
        e.OccurredAt = s.OccurredAt.ToUniversalTime(); e.LocalTime = s.LocalTime;
        e.TimeZoneSnapshot = s.TimeZoneSnapshot; e.OccurredAtSource = s.OccurredAtSource;
        e.ValueUnitSource = s.ValueUnitSource; e.SourceKind = s.SourceKind; e.SourceId = s.SourceId;
        e.CandidateOrdinal = s.CandidateOrdinal; e.TextSourceId = s.TextSourceId;
        e.PhotoSourceId = s.PhotoSourceId; e.PhotoBatchId = s.PhotoBatchId;
        e.InputRevisionId = s.InputRevisionId; e.ExtractionResultId = s.ExtractionResultId;
        e.SourceAuthorUserId = s.SourceAuthorUserId; e.SourceMessageDbId = s.SourceMessageDbId;
        e.TelegramMessageId = s.TelegramMessageId; e.DeletedAt = s.DeletedAt?.ToUniversalTime();
        e.DeleteReason = s.DeleteReason; e.DeletedByUserId = s.DeletedByUserId;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class VetMutationTracking(AssistantDbContext context) : IDisposable
    {
        public void Dispose()
        {
            // A rollback must not poison this request's later SaveChanges with failed Added or
            // Modified diary entities. Other stores' tracked entries are left alone.
            foreach (var entry in context.ChangeTracker.Entries().Where(e =>
                e.Entity is VetEvent or VetDiaryAction or VetDiaryActionChange).ToArray())
                entry.State = EntityState.Detached;
        }
    }
}
