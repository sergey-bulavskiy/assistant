using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Llm;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet.Photos;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoWork(VetDiaryScope Scope, Guid SourceId, Guid InputRevisionId,
    long ActorUserId, Guid? ScheduledAttemptKey = null)
{
    public bool ArchiveOnly { get; init; }
}
public sealed record VetPhotoRuntimeOptions(bool SubscriptionOnly,
    string ImageModel = "gpt-6.1-sol");
public enum VetPhotoProcessStatus { Deferred, Review, Failed, Unknown }
public sealed record VetPhotoProcessResult(VetPhotoProcessStatus Status, string? Category = null,
    VetPhotoExtraction? Extraction = null, VetPhotoCandidateDelta? Delta = null);

public interface IVetPhotoDispatchStore
{
    Task<VetPhotoInputRevision?> ReadInputAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPhotoWork>> GetDueAsync(long familyId, long botDbId, int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<VetPhotoWork>> GetRetainedDueAsync(long familyId, long botDbId, int limit,
        CancellationToken cancellationToken);
    Task<VetPhotoImageResult?> ReadStoredImageAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, Guid? scheduledAttemptKey, long actorUserId, CancellationToken cancellationToken);
}

public sealed class VetPhotoProcessor(IVetPhotoArchiveStore archive,
    IVetPhotoExtractionStore images, IVetPhotoDispatchStore dispatch, IVetPhotoImageDecoder decoder, ILlmGateway gateway,
    IRolePrompts prompts, VetPhotoRuntimeOptions options, ILogger<VetPhotoProcessor> logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<VetPhotoProcessResult> ProcessAsync(VetPhotoWork work, ITelegramClient telegram, CancellationToken ct)
    {
        if (work.SourceId == Guid.Empty || work.InputRevisionId == Guid.Empty || work.ActorUserId <= 0)
            return new(VetPhotoProcessStatus.Failed, "invalid_source");
        var source = await archive.GetSourceAsync(work.Scope, work.SourceId, ct);
        if (source?.Source?.SourceMessageDbId == null || source.Input == null
            || source.Source.CurrentInputRevisionId != source.Input.Id)
            return new(VetPhotoProcessStatus.Deferred, "unbound_source");
        if (!work.ArchiveOnly && work.ScheduledAttemptKey == null && source.Input.Id != work.InputRevisionId
            || work.ArchiveOnly && work.ScheduledAttemptKey != null)
            return new(VetPhotoProcessStatus.Deferred, "stale_input");
        var stored = work.ArchiveOnly ? null : await dispatch.ReadStoredImageAsync(work.Scope, work.SourceId, work.InputRevisionId,
            work.ScheduledAttemptKey, work.ActorUserId, ct);
        if (stored != null) return ImageResult(stored);
        if (work.ScheduledAttemptKey == null)
        {
            var download = await archive.ReserveDownloadAsync(work.Scope, work.SourceId, work.InputRevisionId, work.ActorUserId, ct);
            if (download.Status == VetPhotoArchiveStatus.Reserved && download.Claim is { } claim)
            {
                var copied = await DownloadAsync(work, claim, telegram, ct);
                if (copied != null) return copied;
            }
            else if (download.Status is not (VetPhotoArchiveStatus.Existing or VetPhotoArchiveStatus.Retained))
                return new(VetPhotoProcessStatus.Failed, download.Status switch
                { VetPhotoArchiveStatus.CapacityFull => "archive_full", VetPhotoArchiveStatus.OriginalDeleted => "reupload_required",
                    VetPhotoArchiveStatus.InvalidImage => "encoded_image_too_large", _ => "download_unavailable" });
        }
        if (work.ArchiveOnly) return new(VetPhotoProcessStatus.Deferred, "archive_retained");
        if (!options.SubscriptionOnly || !gateway.IsEnabled || options.ImageModel != "gpt-6.1-sol"
            || !gateway.DescribeModels().Any(m => m.Name == options.ImageModel && m.IsAvailable && m.SupportsImages)
            || prompts.Find("vet", "photo-extract.md") is not { } prompt)
            return new(VetPhotoProcessStatus.Failed, "image_provider_unavailable");
        var promptVersion = "v1-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))[..20])
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (work.ScheduledAttemptKey == null && source.Input.ReusesImageInputId != null)
        {
            var reused = await images.ReuseDisplayAsync(work.Scope, work.SourceId, work.InputRevisionId,
                work.ActorUserId, options.ImageModel, ct, promptVersion);
            if (reused.Extraction != null) return ImageResult(reused);
            if (reused.Status is VetPhotoImageStatus.CapacityFull or VetPhotoImageStatus.Busy)
                return new(VetPhotoProcessStatus.Deferred, reused.Status == VetPhotoImageStatus.CapacityFull ? "archive_full" : "busy");
        }
        var image = work.ScheduledAttemptKey is { } key
            ? await images.ClaimScheduledImageAsync(work.Scope, key, work.ActorUserId, ct)
            : await images.ClaimCurrentImageAsync(work.Scope, work.SourceId, work.InputRevisionId, work.ActorUserId, ct);
        if (image.Extraction != null) return ImageResult(image);
        if (image.Status != VetPhotoImageStatus.Claimed || image.Claim is not { } owned)
            return new(image.Status == VetPhotoImageStatus.Unknown ? VetPhotoProcessStatus.Unknown : VetPhotoProcessStatus.Deferred,
                image.Status switch { VetPhotoImageStatus.Unknown => "outcome_unknown", VetPhotoImageStatus.CapacityFull => "archive_full", _ => "busy_or_stale" });
        if (owned.SourceId != work.SourceId || owned.InputRevisionId != work.InputRevisionId
            || owned.Scope != work.Scope || owned.ActorUserId != work.ActorUserId
            || work.ScheduledAttemptKey is { } selectedKey && owned.AttemptKey != selectedKey)
            return new(VetPhotoProcessStatus.Deferred, "stale_input");
        VetPhotoOriginalRead? original = null;
        var crossed = false;
        try
        {
            var selectedInput = await dispatch.ReadInputAsync(work.Scope, work.SourceId, work.InputRevisionId, work.ActorUserId, ct);
            if (selectedInput == null)
            {
                await images.RecordImageFailureAsync(work.Scope, owned.AttemptKey, owned.ClaimToken, work.ActorUserId,
                    "provider_unavailable", VetPhotoImageFailureDisposition.KnownNotDispatched, ct);
                return new(VetPhotoProcessStatus.Deferred, "stale_input");
            }
            original = await archive.ReadOriginalAsync(work.Scope, work.InputRevisionId, work.ActorUserId,
                owned.AttemptKey, owned.ClaimToken, owned.LeaseUntil, ct);
            if (original == null)
            {
                await images.RecordImageFailureAsync(work.Scope, owned.AttemptKey, owned.ClaimToken, work.ActorUserId,
                    "provider_unavailable", VetPhotoImageFailureDisposition.KnownNotDispatched, ct);
                return new(VetPhotoProcessStatus.Failed, "reupload_required");
            }
            // Once marker persistence begins, a lost commit response cannot prove no dispatch marker.
            crossed = true;
            if (!await images.MarkImageDispatchedAsync(work.Scope, owned.AttemptKey, owned.ClaimToken, work.ActorUserId, ct))
            {
                crossed = false;
                await RecordImageFailureSafelyAsync(work, owned, "provider_unavailable", false);
                return new(VetPhotoProcessStatus.Deferred, "stale_input");
            }
            // An explicit successful marker precedes the sole provider call.
            using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(90), time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            var request = new LlmRequest(work.Scope.FamilyId, work.Scope.TelegramBotId, "fast", options.ImageModel,
                prompt, [new(LlmMessageRole.User, JsonSerializer.Serialize(new
                { photo_source_id = work.SourceId, input_revision_id = work.InputRevisionId, untrusted_caption = selectedInput.Caption }))],
                work.Scope.ChatId, work.Scope.TopicId, source.Source.SourceMessageDbId)
                { Images = [new(original.Original, original.Image.MimeType)], AttemptKey = owned.AttemptKey };
            var result = await gateway.CompleteAsync(request, deadline.Token);
            if (!result.IsAnswer || result.Text == null)
            {
                var unknown = result.RefusalReason == LlmRefusalReason.OutcomeUnknown;
                await images.RecordImageFailureAsync(work.Scope, owned.AttemptKey, owned.ClaimToken, work.ActorUserId,
                    unknown ? "outcome_unknown" : "provider_refused", unknown
                        ? VetPhotoImageFailureDisposition.OutcomeUnknown : VetPhotoImageFailureDisposition.KnownNotDispatched, ct);
                return new(unknown ? VetPhotoProcessStatus.Unknown : VetPhotoProcessStatus.Failed,
                    unknown ? "outcome_unknown" : "image_provider_refused");
            }
            var completed = await images.CompleteImageAsync(new(work.Scope, owned.AttemptKey, owned.ClaimToken,
                work.ActorUserId, work.SourceId, work.InputRevisionId, result.ModelName ?? options.ImageModel,
                result.Text, result.TraceAttemptId) { PromptVersion = promptVersion }, ct);
            return ImageResult(completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordImageFailureSafelyAsync(work, owned, crossed ? "outcome_unknown" : "provider_cancelled", crossed);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Photo image processing deferred: {ExceptionType}", ex.GetType().Name);
            await RecordImageFailureSafelyAsync(work, owned, crossed ? "outcome_unknown" : "provider_unavailable", crossed);
            return new(crossed ? VetPhotoProcessStatus.Unknown : VetPhotoProcessStatus.Deferred,
                crossed ? "outcome_unknown" : "image_provider_unavailable");
        }
        finally
        {
            if (original != null)
            {
                Array.Clear(original.Original);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
                try { await archive.ReleaseReaderAsync(work.Scope, original.ReaderLeaseId, cleanup.Token); }
                catch (Exception ex) { logger.LogWarning("Photo reader release deferred: {ExceptionType}", ex.GetType().Name); }
            }
        }
    }

    private static VetPhotoProcessResult ImageResult(VetPhotoImageResult result) => result.Status switch
    {
        VetPhotoImageStatus.InvalidResult => new(VetPhotoProcessStatus.Failed, "invalid_result", result.Extraction, result.Delta),
        VetPhotoImageStatus.Unknown => new(VetPhotoProcessStatus.Unknown, "outcome_unknown", result.Extraction, result.Delta),
        VetPhotoImageStatus.Installed or VetPhotoImageStatus.Existing or VetPhotoImageStatus.Reused
            or VetPhotoImageStatus.ProposedDelta or VetPhotoImageStatus.EvidenceOnly when result.Extraction != null =>
                new(VetPhotoProcessStatus.Review, Extraction: result.Extraction, Delta: result.Delta),
        VetPhotoImageStatus.CapacityFull => new(VetPhotoProcessStatus.Deferred, "archive_full"),
        _ => new(VetPhotoProcessStatus.Deferred, "busy_or_stale")
    };

    private async Task<VetPhotoProcessResult?> DownloadAsync(VetPhotoWork work, VetPhotoDownloadClaim claim,
        ITelegramClient telegram, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        try
        {
            using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            var count = await telegram.DownloadFileAsync(claim.Input.FileId, bytes, VetPhotoImageLimits.MaxEncodedBytes, deadline.Token);
            if (count != bytes.Length) throw new InvalidOperationException("Photo byte count is invalid.");
            var original = bytes.ToArray();
            try
            {
                var decoded = decoder.Decode(original, ct);
                if (decoded.Image == null)
                {
                    await archive.RecordDownloadFailureAsync(work.Scope, claim.Attempt.Id, claim.ClaimToken,
                        decoded.FailureReason ?? "invalid_image", false, ct);
                    return new(VetPhotoProcessStatus.Failed, decoded.FailureReason ?? "invalid_image");
                }
                var committed = await archive.CommitOriginalAsync(new(work.Scope, work.ActorUserId,
                    claim.Attempt.Id, claim.ClaimToken, work.InputRevisionId, original, decoded.Image), ct);
                if (committed.Status is VetPhotoArchiveStatus.Retained or VetPhotoArchiveStatus.Existing) return null;
                await RecordDownloadFailureSafelyAsync(work, claim, "download_unavailable", false);
                return new(VetPhotoProcessStatus.Deferred, committed.Status == VetPhotoArchiveStatus.CapacityFull ? "archive_full" : "stale_input");
            }
            finally { Array.Clear(original); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordDownloadFailureSafelyAsync(work, claim, "download_cancelled", false);
            throw;
        }
        catch (TelegramFileDownloadException ex)
        {
            var reason = ex.Reason switch { TelegramFileDownloadFailure.TooLarge => "download_too_large",
                TelegramFileDownloadFailure.Timeout => "download_timeout", _ => "download_unavailable" };
            await archive.RecordDownloadFailureAsync(work.Scope, claim.Attempt.Id, claim.ClaimToken,
                reason, ex.Reason == TelegramFileDownloadFailure.Timeout, ct);
            return new(VetPhotoProcessStatus.Failed, reason);
        }
        catch (OperationCanceledException)
        {
            await archive.RecordDownloadFailureAsync(work.Scope, claim.Attempt.Id, claim.ClaimToken, "download_timeout", true, ct);
            return new(VetPhotoProcessStatus.Deferred, "download_timeout");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Photo download deferred: {ExceptionType}", ex.GetType().Name);
            await RecordDownloadFailureSafelyAsync(work, claim, "download_unavailable", false);
            return new(VetPhotoProcessStatus.Failed, "download_unavailable");
        }
        finally
        {
            if (bytes.TryGetBuffer(out var buffer) && buffer.Array != null) Array.Clear(buffer.Array);
        }
    }

    private async Task RecordDownloadFailureSafelyAsync(VetPhotoWork work, VetPhotoDownloadClaim claim, string category, bool transient)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        try { await archive.RecordDownloadFailureAsync(work.Scope, claim.Attempt.Id, claim.ClaimToken, category, transient, cleanup.Token); }
        catch (Exception ex) { logger.LogWarning("Photo download state deferred: {ExceptionType}", ex.GetType().Name); }
    }
    private async Task RecordImageFailureSafelyAsync(VetPhotoWork work, VetPhotoImageClaim claim, string category, bool unknown)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        try { await images.RecordImageFailureAsync(work.Scope, claim.AttemptKey, claim.ClaimToken, work.ActorUserId,
            category, unknown ? VetPhotoImageFailureDisposition.OutcomeUnknown : VetPhotoImageFailureDisposition.KnownNotDispatched, cleanup.Token); }
        catch (Exception ex) { logger.LogWarning("Photo image state deferred: {ExceptionType}", ex.GetType().Name); }
    }
}
