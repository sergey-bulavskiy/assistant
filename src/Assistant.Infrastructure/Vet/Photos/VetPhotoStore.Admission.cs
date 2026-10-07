using System.Text;
using System.Text.Json;
using System.Buffers;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Messages;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore
{
    public async Task<VetPhotoAdmission> AdmitAsync(
        VetDiaryScope scope, IncomingMessage message, long updateId,
        VetPhotoAttachment attachment, Guid? textInputRevisionId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        attachment = attachment with
        {
            FileUniqueId = BoundedHint(attachment.FileUniqueId, 2048),
            FileName = BoundedHint(attachment.FileName, 256),
            ReportedMimeType = BoundedHint(attachment.ReportedMimeType, 128)
        };
        if (message.ChatId != scope.ChatId || message.TopicId != scope.TopicId
            || message.UserId is not { } author || message.Kind == MessageKind.Service
            || !MetadataValid(message, attachment))
            return new(VetPhotoAdmissionStatus.InvalidMetadata, null, null);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            if (!await ActorAsync(scope, author, ct, message.ChatType))
                return new(VetPhotoAdmissionStatus.Refused, null, null);
            var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(
                s => s.TelegramMessageId == message.MessageId && s.SourceSlot == 1, ct);
            var priorUpdate = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking().SingleOrDefaultAsync(
                r => r.UpdateId == updateId && (source == null || r.SourceId == source.Id), ct);
            if (priorUpdate is not null)
            {
                if (source is null || priorUpdate.SourceId != source.Id)
                    throw new InvalidOperationException("Photo admission key conflict.");
                return Admission(source, priorUpdate);
            }
            var fingerprint = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                attachment.FileId, attachment.FileUniqueId, attachment.FileName,
                attachment.ReportedMimeType, attachment.ReportedSize,
                attachment.ReportedWidth, attachment.ReportedHeight,
                Caption = message.Text ?? ""
            }, Json)));
            VetPhotoInputRevision? previous = null;
            if (source is not null)
            {
                if (source.SourceAuthorUserId != author)
                    return new(VetPhotoAdmissionStatus.Refused, null, null);
                previous = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking()
                    .SingleAsync(r => r.Id == source.CurrentInputRevisionId, ct);
                if (!message.IsEdit || previous.InputFingerprint == fingerprint
                    || message.EditedAt is { } edited && previous.EditedAt is { } oldEdit && edited <= oldEdit)
                    return Admission(source, previous);
                db.Attach(source);
                source.State = source.State is "late" or "full" ? source.State : "admitted";
                await Scoped<VetPhotoCandidate>(scope).Where(c => c.SourceId == source.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1)
                        .SetProperty(c => c.RequiresExplicitRestoration,
                            c => c.RequiresExplicitRestoration || c.State == "excluded" || c.State == "cancelled")
                        .SetProperty(c => c.State, "waiting")
                        .SetProperty(c => c.UpdatedAt, clock.UtcNow), ct);
            }
            else
            {
                var collecting = await Scoped<VetPhotoBatch>(scope).AsNoTracking()
                    .SingleOrDefaultAsync(b => b.State == "collecting", ct);
                var relatedIds = await Scoped<VetPhotoSource>(scope)
                    .Where(s => s.BatchId != null
                        && (message.MediaGroupId != null && s.MediaGroupId == message.MediaGroupId
                            || message.ReplyToMessageId != null && s.TelegramMessageId == message.ReplyToMessageId))
                    .Select(s => s.BatchId!.Value).Distinct().ToListAsync(ct);
                var causal = await Scoped<VetPhotoBatch>(scope).AsNoTracking()
                    .Where(b => b.State != "collecting"
                        && (relatedIds.Contains(b.Id)
                            || collecting == null && b.IntakeKind == "collection" && b.IntakeClosedAt != null
                                && message.SentAt >= b.IntakeOpenedAt && message.SentAt <= b.IntakeClosedAt))
                    .OrderByDescending(b => b.CreatedAt).Take(2).ToListAsync(ct);
                VetPhotoBatch? batch = null;
                Guid? proposed = causal.Count == 1 ? causal[0].Id : null;
                var association = causal.Count > 0 ? "late" : "single";
                var status = causal.Count > 0 ? "late" : "admitted";
                int? itemNumber = null;
                if (causal.Count == 0 && collecting is not null)
                {
                    var count = await Scoped<VetPhotoSource>(scope).CountAsync(s => s.BatchId == collecting.Id, ct);
                    if (count >= 50)
                    {
                        proposed = collecting.Id;
                        association = "batch_full";
                        status = "full";
                    }
                    else
                    {
                        batch = collecting;
                        itemNumber = collecting.NextItemNumber;
                        association = "collecting";
                        await Scoped<VetPhotoBatch>(scope).Where(b => b.Id == batch.Id)
                            .ExecuteUpdateAsync(u => u.SetProperty(b => b.NextItemNumber, b => b.NextItemNumber + 1), ct);
                    }
                }
                else if (causal.Count == 0)
                {
                    var profile = await db.Set<VetProfile>().AsNoTracking().SingleOrDefaultAsync(
                        p => p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct);
                    if (profile is null)
                    {
                        profile = new VetProfile { FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, UpdatedAt = clock.UtcNow };
                        db.Add(profile);
                        await db.SaveChangesAsync(ct);
                    }
                    batch = InScope(new VetPhotoBatch
                    {
                        Id = Guid.NewGuid(), ProfileId = profile.Id, ProfileRevision = profile.Revision,
                        StarterUserId = author, State = "closed", IntakeKind = "single", CreatedAt = clock.UtcNow,
                        AssumptionsJson = JsonSerializer.Serialize(new VetPhotoBatchAssumptions(
                            profile.TimeZone, profile.GlucoseUnit, null, null, null, false, false, false), Json),
                        ClosedAt = clock.UtcNow, IntakeOpenedAt = message.SentAt,
                        IntakeClosedAt = message.SentAt, NextItemNumber = 2, UpdatedAt = clock.UtcNow
                    }, scope);
                    db.Add(batch);
                    itemNumber = 1;
                }
                source = InScope(new VetPhotoSource
                {
                    Id = Guid.NewGuid(), BatchId = batch?.Id, ProposedBatchId = proposed,
                    TelegramMessageId = message.MessageId, ChatType = message.ChatType,
                    SourceAuthorUserId = author, MediaGroupId = message.MediaGroupId,
                    ReplyToMessageId = message.ReplyToMessageId, ItemNumber = itemNumber,
                    Association = association, State = status,
                    SentAt = message.SentAt, AdmittedAt = clock.UtcNow
                }, scope);
                db.Add(source);
                db.Add(InScope(new VetPhotoCandidate
                {
                    Id = Guid.NewGuid(), SourceId = source.Id, BatchId = source.BatchId,
                    State = status is "late" or "full" ? status : "waiting", UpdatedAt = clock.UtcNow
                }, scope));
            }
            var input = InScope(new VetPhotoInputRevision
            {
                Id = Guid.NewGuid(), SourceId = source.Id, Ordinal = source.CurrentOrdinal + 1,
                UpdateId = updateId, IsEdit = message.IsEdit, EditedAt = message.EditedAt,
                FileId = attachment.FileId, FileUniqueId = attachment.FileUniqueId,
                FileName = attachment.FileName, ReportedMimeType = attachment.ReportedMimeType,
                ReportedSize = attachment.ReportedSize, ReportedWidth = attachment.ReportedWidth,
                ReportedHeight = attachment.ReportedHeight, Caption = message.Text ?? "",
                TextInputRevisionId = textInputRevisionId, InputFingerprint = fingerprint, ReceivedAt = clock.UtcNow,
                ReusesImageInputId = previous is not null && previous.FileId == attachment.FileId
                    && previous.FileUniqueId == attachment.FileUniqueId ? previous.Id : null
            }, scope);
            db.Add(input);
            if (source.State is not ("late" or "full"))
                db.Add(InScope(new VetPhotoAttempt
                {
                    Id = Guid.NewGuid(), SourceId = source.Id, InputRevisionId = input.Id,
                    ActorUserId = author, ExpectedSourceOrdinal = input.Ordinal,
                    ExpectedCurrentInputId = input.Id, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow
                }, scope));
            source.CurrentInputRevisionId = input.Id;
            source.CurrentOrdinal = input.Ordinal;
            await db.SaveChangesAsync(ct);
            await InvalidateBatchAsync(scope, source.BatchId, ct);
            await tx.CommitAsync(ct);
            return Admission(source, input) with
            {
                Status = source.State switch
                {
                    "late" => VetPhotoAdmissionStatus.Late,
                    "full" => VetPhotoAdmissionStatus.Full,
                    _ => VetPhotoAdmissionStatus.Admitted
                }
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }

    private static bool MetadataValid(IncomingMessage message, VetPhotoAttachment attachment) =>
        message.MessageId > 0 && message.SentAt.Offset == TimeSpan.Zero
        && (message.EditedAt == null || message.EditedAt.Value.Offset == TimeSpan.Zero)
        && (message.Text?.Length ?? 0) <= 4096
        && attachment.FileId is { Length: > 0 and <= 2048 }
        && ScalarText(attachment.FileId) && ScalarText(message.Text)
        && ScalarText(message.MediaGroupId)
        && (attachment.FileUniqueId?.Length ?? 0) <= 2048
        && (attachment.FileName?.Length ?? 0) <= 256
        && (attachment.ReportedMimeType?.Length ?? 0) <= 128
        && (message.MediaGroupId?.Length ?? 0) <= 256;

    private static bool ScalarText(string? value)
    {
        if (value is null) return true;
        var span = value.AsSpan();
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != OperationStatus.Done
                || rune.Value == 0)
                return false;
            span = span[consumed..];
        }
        return true;
    }

    private static string? BoundedHint(string? value, int maxChars)
    {
        if (value is null) return null;
        var result = new StringBuilder(Math.Min(value.Length, maxChars));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (result.Length + rune.Utf16SequenceLength > maxChars) break;
            result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }

    private static VetPhotoAdmission Admission(VetPhotoSource source, VetPhotoInputRevision input) =>
        new(source.State switch
        {
            "late" => VetPhotoAdmissionStatus.Late,
            "full" => VetPhotoAdmissionStatus.Full,
            _ => VetPhotoAdmissionStatus.Existing
        }, source, input);

    public async Task<bool> BindMessageAsync(
        VetDiaryScope scope, Guid sourceId, long sourceMessageDbId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null || source.SourceMessageDbId is { } old && old != sourceMessageDbId)
            return false;
        if (!await db.Messages.AnyAsync(m => m.Id == sourceMessageDbId
                && m.FamilyId == scope.FamilyId && m.BotId == scope.TelegramBotId
                && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId
                && m.TelegramMessageId == source.TelegramMessageId
                && m.UserId == source.SourceAuthorUserId && m.Direction == MessageDirection.In
                && m.ChatType == source.ChatType && m.SentAt == source.SentAt
                && (m.Kind == MessageKind.Photo || m.Kind == MessageKind.Document), ct))
            return false;
        await Scoped<VetPhotoSource>(scope).Where(s => s.Id == sourceId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMessageDbId, sourceMessageDbId), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<VetPhotoAdmission?> FindSourceAsync(
        VetDiaryScope scope, int telegramMessageId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var source = await Scoped<VetPhotoSource>(scope).AsNoTracking()
            .SingleOrDefaultAsync(s => s.TelegramMessageId == telegramMessageId && s.SourceSlot == 1, ct);
        return source is null ? null : await ReadAdmissionAsync(scope, source, ct);
    }

    public async Task<VetPhotoAdmission?> GetSourceAsync(
        VetDiaryScope scope, Guid sourceId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId, ct);
        return source is null ? null : await ReadAdmissionAsync(scope, source, ct);
    }

    private async Task<VetPhotoAdmission> ReadAdmissionAsync(
        VetDiaryScope scope, VetPhotoSource source, CancellationToken ct) =>
        Admission(source, await Scoped<VetPhotoInputRevision>(scope).AsNoTracking()
            .SingleAsync(r => r.Id == source.CurrentInputRevisionId, ct));

    public async Task<IReadOnlyList<VetPhotoAdmission>> GetUnboundAsync(
        long familyId, long botDbId, int limit, CancellationToken ct)
    {
        await _guard.BotAsync(familyId, botDbId, null, ct);
        var sources = await db.Set<VetPhotoSource>().AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.BotDbId == botDbId && s.SourceMessageDbId == null)
            .OrderBy(s => s.AdmittedAt).ThenBy(s => s.Id).Take(Math.Clamp(limit, 1, 5)).ToListAsync(ct);
        var answer = new List<VetPhotoAdmission>();
        foreach (var source in sources)
            answer.Add(await ReadAdmissionAsync(
                new(familyId, botDbId, source.TelegramBotId, source.ChatId, source.TopicId), source, ct));
        return answer;
    }
}
