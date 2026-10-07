using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health.Documents;

public sealed class HealthDocumentProcessor(
    IHealthDocumentStore store, IDocumentTextExtractor extractor, IHealthDocumentLeaseKeeper leases,
    HealthDocumentExecutionGate gate, IClock clock, ILogger<HealthDocumentProcessor> logger)
{
    public const string CannotSave = "Не удалось сохранить документ. Попробуйте отправить его ещё раз.";
    public const string CannotRead = "Не удалось прочитать файл. Отправьте документ с доступным файлом ещё раз.";
    private readonly HealthReplies _replies = new(logger);

    public async Task ProcessAsync(HealthDocumentAdmissionInfo admission, ITelegramClient telegram, CancellationToken token)
    {
        using var execution = await gate.EnterAsync(admission.Scope.BotDbId, token);
        await ProcessOwnedAsync(admission, telegram, token);
    }

    public async Task ResumeAsync(HealthDocumentScope scope, ITelegramClient telegram, CancellationToken token)
    {
        using var execution = await gate.EnterAsync(scope.BotDbId, token);
        if (!gate.BeginRecovery(scope.BotDbId)) return;
        var due = await store.GetDueAsync(scope, token);
        foreach (var admission in due.Take(HealthDocumentLimits.RecoveryBatchSize))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bound = await store.BindAsync(scope, admission.Id, null, token);
                if (bound?.SourceMessageId is null)
                {
                    await store.DeferAsync(admission, token);
                    continue;
                }
                await ProcessOwnedAsync(bound, telegram, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Health document recovery deferred: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private async Task ProcessOwnedAsync(HealthDocumentAdmissionInfo admission, ITelegramClient telegram, CancellationToken token)
    {
        if (!await store.IsAuthorizedAsync(admission, token))
        {
            await store.PauseAsync(admission, token);
            return;
        }
        var lease = await store.TryClaimAsync(admission, token);
        if (lease is null) return;
        try
        {
            // Metadata has committed. This reaction does not claim that the file text was read.
            if (await store.TryClaimDeliveryAsync(lease.Admission, reaction: true, token))
                await _replies.MarkRecordedAsync(telegram, admission.ChatId, admission.TelegramMessageId, token);
            if (lease.Document.TextStatus != "processing")
            {
                await DeliverNoticeAsync(lease.Admission, lease.Document, telegram, token);
                return;
            }

            bool finalized;
            await using (var owner = await leases.StartAsync(lease, token))
            {
                finalized = await ExtractAsync(lease, telegram, owner.Token);
            }
            token.ThrowIfCancellationRequested();
            if (!finalized) return;
            var current = await store.FindAsync(admission.Scope, admission.ChatId, admission.TopicId, admission.TelegramMessageId, token);
            if (current is null) return;
            var document = await store.GetDocumentAsync(admission.Scope, admission.Id, token);
            if (document is not null) await DeliverNoticeAsync(current, document, telegram, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Lost lease/grant: no late writes or failure notice. The parser task was awaited.
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.ReleaseAsync(lease, cleanup.Token); }
            catch (Exception ex) { logger.LogWarning("Health document lease release deferred: {ExceptionType}", ex.GetType().Name); }
        }
    }

    private async Task<bool> ExtractAsync(HealthDocumentLease lease, ITelegramClient telegram, CancellationToken token)
    {
        var attachment = lease.Admission.Attachment;
        if (!HealthDocumentCandidate.IsSupported(attachment))
            return await store.FinishAsync(lease, null, "metadata_only", "unsupported_format", null, token);
        if (attachment.FileSize > HealthDocumentLimits.MaxBytes)
            return await store.FinishAsync(lease, null, "metadata_only", "too_large", null, token);
        var attempt = await store.TryBeginAttemptAsync(lease, token);
        if (attempt is null)
        {
            if (lease.Admission.AttemptCount >= HealthDocumentLimits.MaxAttempts)
                return await store.FinishAsync(lease, null, "failed", "interrupted", null, token);
            return false;
        }
        await using var bytes = new MemoryStream();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(HealthDocumentLimits.DownloadTimeout);
            var count = await telegram.DownloadFileAsync(attachment.FileId, bytes, HealthDocumentLimits.MaxBytes, deadline.Token);
            if (count > HealthDocumentLimits.MaxBytes || bytes.Length > HealthDocumentLimits.MaxBytes)
                return await store.FinishAsync(lease, null, "metadata_only", "too_large", null, token);
            token.ThrowIfCancellationRequested();
            bytes.Position = 0;
            // Keep execution ownership until this synchronous operation actually returns.
            var extracted = await Task.Run(() => extractor.Extract(bytes, attachment, token), CancellationToken.None);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(extracted.Text))
                return await store.FinishAsync(lease, null, "metadata_only", extracted.FailureReason ?? "no_readable_text", null, token);
            return await store.FinishAsync(lease, extracted, "read", null, null, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return await TransientAsync(lease, attempt.Value, "timeout", token);
        }
        catch (TelegramFileDownloadException ex)
        {
            if (ex.Reason == TelegramFileDownloadFailure.TooLarge)
                return await store.FinishAsync(lease, null, "metadata_only", "too_large", null, token);
            return await TransientAsync(lease, attempt.Value, ex.Reason == TelegramFileDownloadFailure.Timeout ? "timeout" : "unavailable", token);
        }
    }

    private Task<bool> TransientAsync(HealthDocumentLease lease, int attempt, string reason, CancellationToken token) =>
        store.FinishAsync(lease, null, attempt < HealthDocumentLimits.MaxAttempts ? "processing" : "failed", reason,
            attempt < HealthDocumentLimits.MaxAttempts ? clock.UtcNow + HealthDocumentLimits.RetryDelay(attempt) : null, token);

    private async Task DeliverNoticeAsync(HealthDocumentAdmissionInfo admission, HealthDocumentInfo document,
        ITelegramClient telegram, CancellationToken token)
    {
        var text = document.TextStatus == "read"
            ? document.TextTruncated ? "Сохранил документ; прочитал только начало: текст превышает лимит." : null
            : document.FailureReason switch
            {
                "unsupported_format" or "no_readable_text" => "Сохранил, но текст прочитать не могу — фото и сканы будут позже.",
                "too_large" => "Сохранил сведения о документе, но файл больше 20 МБ. Отправьте текстовый PDF или UTF-8 файл меньшего размера.",
                "unavailable" or "timeout" or "interrupted" => document.TextStatus == "processing"
                    ? "Сохранил документ, но загрузить текст пока не удалось. Повторю в пределах трёх попыток; состояние — /docs."
                    : "Сохранил сведения о документе, но загрузить текст не удалось. Отправьте файл ещё раз; состояние — /docs.",
                _ => "Сохранил документ, но текст прочитать не удалось. Отправьте читаемый текстовый PDF или UTF-8 файл."
            };
        if (text is not null && await store.TryClaimDeliveryAsync(admission, reaction: false, token))
            await _replies.SendAsync(telegram, admission.ChatId, admission.TopicId, text, admission.TelegramMessageId, token);
    }
}
