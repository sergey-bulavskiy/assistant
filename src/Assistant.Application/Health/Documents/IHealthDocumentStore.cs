using Assistant.Application.Telegram;

namespace Assistant.Application.Health.Documents;

public interface IHealthDocumentStore
{
    Task<HealthDocumentAdmissionInfo?> AdmitAsync(HealthDocumentScope scope, IncomingMessage message, long updateId, CancellationToken token);
    Task<HealthDocumentAdmissionInfo?> FindAsync(HealthDocumentScope scope, long chatId, int? topicId, int messageId, CancellationToken token);
    Task<HealthDocumentAdmissionInfo?> BindAsync(HealthDocumentScope scope, Guid admissionId, long? messageDbId, CancellationToken token);
    Task<IReadOnlyList<HealthDocumentAdmissionInfo>> GetDueAsync(HealthDocumentScope scope, CancellationToken token);
    Task<bool> IsAuthorizedAsync(HealthDocumentAdmissionInfo admission, CancellationToken token);
    Task PauseAsync(HealthDocumentAdmissionInfo admission, CancellationToken token);
    Task DeferAsync(HealthDocumentAdmissionInfo admission, CancellationToken token);
    Task<HealthDocumentLease?> TryClaimAsync(HealthDocumentAdmissionInfo admission, CancellationToken token);
    Task<int?> TryBeginAttemptAsync(HealthDocumentLease lease, CancellationToken token);
    Task<bool> RenewAsync(HealthDocumentLease lease, CancellationToken token);
    Task ReleaseAsync(HealthDocumentLease lease, CancellationToken token);
    Task<bool> FinishAsync(HealthDocumentLease lease, DocumentTextExtraction? result, string textStatus, string? failureReason, DateTimeOffset? retryAt, CancellationToken token);
    Task<bool> TryClaimDeliveryAsync(HealthDocumentAdmissionInfo admission, bool reaction, CancellationToken token);
    Task<bool> HasAcknowledgedDocumentAsync(HealthDocumentScope scope, long chatId, int messageId, CancellationToken token);
    Task<IReadOnlyList<HealthDocumentInfo>> GetLatestAsync(HealthDocumentScope scope, CancellationToken token);
    Task<HealthDocumentInfo?> GetDocumentAsync(HealthDocumentScope scope, Guid admissionId, CancellationToken token);
    Task<HealthDocumentContextSnapshot> GetContextAsync(HealthDocumentScope scope, CancellationToken token);
    Task<HealthDocumentSourceDeletion> DeleteSourceAsync(HealthDocumentScope scope, long chatId, int? topicId, int messageId, long? actorId, CancellationToken token);
}
