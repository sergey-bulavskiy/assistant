using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Domain.Messages;

namespace Assistant.Application.Memory;

public sealed record GeneralMemoryScope(long FamilyId, long BotDbId, long BotId, long ChatId,
    int? TopicId, long ActorUserId, string ChatType);
public sealed record GeneralFactInfo(long Id, string Text);
public sealed record GeneralSummaryInfo(string Text, long ThroughMessageId);
public sealed record GeneralMemorySnapshot(IReadOnlyList<GeneralFactInfo> Facts, GeneralSummaryInfo? Summary);
public sealed record GeneralSearchHit(long MessageId, int TelegramMessageId, string Text, DateTimeOffset SentAt);
public sealed record GeneralSummarySource(long Id, MessageDirection Direction, string Text,
    DateTimeOffset? EditedAt, DateTimeOffset? SentAt = null, string? Username = null, long? UserId = null);
public sealed record GeneralSummaryFold(long ResetCutoff, long ThroughMessageId, long SourceVersion,
    string PreviousText, IReadOnlyList<GeneralSummarySource> Sources)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Sources))));
}
public sealed record GeneralRememberResult(long? FactId, string? Failure, bool IsRetired = false);

public interface IGeneralMemoryStore
{
    Task<IReadOnlyList<GeneralSearchHit>> SearchAsync(GeneralMemoryScope scope, string query,
        CancellationToken cancellationToken);
    Task<GeneralRememberResult> RememberAsync(GeneralMemoryScope scope, long sourceMessageId, string text,
        CancellationToken cancellationToken);
    Task<bool> ForgetAsync(GeneralMemoryScope scope, long factId, CancellationToken cancellationToken);
    Task<GeneralMemorySnapshot> ReadAsync(GeneralMemoryScope scope, CancellationToken cancellationToken);
    Task<GeneralSummaryFold?> PrepareFoldAsync(GeneralMemoryScope scope, long beforeMessageId,
        int recentCount, CancellationToken cancellationToken);
    Task<bool> CommitFoldAsync(GeneralMemoryScope scope, GeneralSummaryFold fold, string text,
        string modelName, CancellationToken cancellationToken);
}
