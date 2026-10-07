using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public sealed class FakeHealthDocumentStore : IHealthDocumentStore
{
    public HealthDocumentAdmissionInfo? Admission { get; set; }
    public HealthDocumentInfo? Document { get; set; }
    public IReadOnlyList<HealthDocumentInfo>? Inventory { get; set; }
    public IReadOnlyList<HealthDocumentAdmissionInfo>? Due { get; set; }
    public HealthDocumentSourceDeletion Deletion { get; set; } = HealthDocumentSourceDeletion.None;
    public bool Authorized { get; set; } = true;
    public bool Deleted { get; private set; }
    public bool Renewed { get; set; } = true;
    public Exception? ClaimFailure { get; set; }
    public Action? OnDelete { get; set; }
    public List<(string Status, string? Reason, DateTimeOffset? RetryAt, string? Text)> Finishes { get; } = [];
    public List<Guid> Releases { get; } = [];
    public int DueReads { get; private set; }
    public int Pauses { get; private set; }
    public int Deferrals { get; private set; }
    public int Bindings { get; private set; }
    public int Attempts { get; private set; }
    public TaskCompletionSource RenewalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid? _lease;

    public Task<HealthDocumentAdmissionInfo?> AdmitAsync(HealthDocumentScope scope, IncomingMessage message, long updateId, CancellationToken token)
    {
        Admission ??= new HealthDocumentAdmissionInfo(Guid.NewGuid(), scope, message.ChatId, message.TopicId, message.ChatType,
            message.MessageId, message.UserId, message.SentAt, message.Document!, message.Text, null, "admitted", 0, null, false, false);
        return Task.FromResult<HealthDocumentAdmissionInfo?>(Admission);
    }
    public Task<HealthDocumentAdmissionInfo?> FindAsync(HealthDocumentScope scope, long chatId, int? topicId, int messageId, CancellationToken token) =>
        Task.FromResult(Admission is { } a && a.Scope == scope && a.ChatId == chatId && a.TopicId == topicId && a.TelegramMessageId == messageId ? a : null);
    public Task<HealthDocumentAdmissionInfo?> BindAsync(HealthDocumentScope scope, Guid id, long? sourceId, CancellationToken token)
    {
        Bindings++;
        if (Admission is { } a && a.Scope == scope && a.Id == id && sourceId is not null) Admission = a with { SourceMessageId = sourceId };
        return Task.FromResult(Admission);
    }
    public Task<IReadOnlyList<HealthDocumentAdmissionInfo>> GetDueAsync(HealthDocumentScope scope, CancellationToken token)
    {
        DueReads++;
        return Task.FromResult(Due ?? (Admission is { } a ? new[] { a } : []));
    }
    public Task<bool> IsAuthorizedAsync(HealthDocumentAdmissionInfo admission, CancellationToken token) => Task.FromResult(Authorized && !Deleted);
    public Task PauseAsync(HealthDocumentAdmissionInfo admission, CancellationToken token) { Pauses++; return Task.CompletedTask; }
    public Task DeferAsync(HealthDocumentAdmissionInfo admission, CancellationToken token) { Deferrals++; return Task.CompletedTask; }
    public Task<HealthDocumentLease?> TryClaimAsync(HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        if (ClaimFailure is not null) throw ClaimFailure;
        if (Deleted || _lease is not null || admission.SourceMessageId is null) return Task.FromResult<HealthDocumentLease?>(null);
        Document ??= new HealthDocumentInfo(1, admission.SourceMessageId.Value, admission.SentAt, admission.Attachment.FileName,
            admission.Caption, "processing", null, false, "processing", null);
        _lease = Guid.NewGuid();
        return Task.FromResult<HealthDocumentLease?>(new(_lease.Value, admission, Document));
    }
    public Task<int?> TryBeginAttemptAsync(HealthDocumentLease lease, CancellationToken token)
    {
        if (Deleted || _lease != lease.LeaseId || (Admission?.AttemptCount ?? 0) >= HealthDocumentLimits.MaxAttempts)
            return Task.FromResult<int?>(null);
        Attempts++;
        Admission = Admission! with { AttemptCount = Admission.AttemptCount + 1 };
        return Task.FromResult<int?>(Admission.AttemptCount);
    }
    public Task<bool> RenewAsync(HealthDocumentLease lease, CancellationToken token)
    {
        RenewalObserved.TrySetResult();
        return Task.FromResult(Renewed && Authorized && !Deleted);
    }
    public Task ReleaseAsync(HealthDocumentLease lease, CancellationToken token)
    {
        Releases.Add(lease.LeaseId);
        if (_lease == lease.LeaseId) _lease = null;
        return Task.CompletedTask;
    }
    public Task<bool> FinishAsync(HealthDocumentLease lease, DocumentTextExtraction? result, string status, string? reason, DateTimeOffset? retry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Deleted || _lease != lease.LeaseId) return Task.FromResult(false);
        Finishes.Add((status, reason, retry, result?.Text));
        Document = Document! with { TextStatus = status, FailureReason = reason, TextTruncated = result?.Truncated ?? false,
            Text = result?.Text, NextAttemptAt = retry, AdmissionStatus = retry is null ? "completed" : "admitted" };
        Admission = Admission! with { Status = Document.AdmissionStatus, NextAttemptAt = retry };
        return Task.FromResult(true);
    }
    public Task<bool> TryClaimDeliveryAsync(HealthDocumentAdmissionInfo admission, bool reaction, CancellationToken token)
    {
        if (!Authorized || Deleted || Admission is null || (reaction ? Admission.ReactionAttempted : Admission.NoticeAttempted)) return Task.FromResult(false);
        Admission = reaction ? Admission with { ReactionAttempted = true } : Admission with { NoticeAttempted = true };
        return Task.FromResult(true);
    }
    public Task<bool> HasAcknowledgedDocumentAsync(HealthDocumentScope scope, long chatId, int messageId, CancellationToken token) =>
        Task.FromResult(!Deleted && Document is not null && Admission is { ReactionAttempted: true });
    public Task<IReadOnlyList<HealthDocumentInfo>> GetLatestAsync(HealthDocumentScope scope, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<HealthDocumentInfo>>(Inventory ?? (Document is { } d && !Deleted ? new[] { d } : []));
    public Task<HealthDocumentInfo?> GetDocumentAsync(HealthDocumentScope scope, Guid admissionId, CancellationToken token) =>
        Task.FromResult(!Deleted ? Document : null);
    public Task<HealthDocumentContextSnapshot> GetContextAsync(HealthDocumentScope scope, CancellationToken token) =>
        Task.FromResult(new HealthDocumentContextSnapshot(Inventory ?? (Document is { } d && !Deleted ? new[] { d } : [])));
    public Task<HealthDocumentSourceDeletion> DeleteSourceAsync(HealthDocumentScope scope, long chatId, int? topicId, int messageId, long? actor, CancellationToken token)
    {
        if (Deleted) return Task.FromResult(HealthDocumentSourceDeletion.None);
        Deleted = true;
        OnDelete?.Invoke();
        return Task.FromResult(Deletion);
    }
}
