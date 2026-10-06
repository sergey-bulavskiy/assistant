using Assistant.Application.Telegram;

namespace Assistant.Application.Diagnostics;

public sealed record TraceStart(
    Guid TraceId, long FamilyId, long BotId, long ChatId, int? TopicId,
    long? UpdateId, long? SourceMessageId, bool IsEdit, string? SourceText, string Kind,
    string? BuildIdentity = null);

public sealed record TraceModelMessage(string Role, string Text, string? Author);

public sealed record TraceModelOptions(
    string Model, double? Temperature, int? MaxOutputTokens, string? ResponseFormat,
    int? MaxOutputChars, int? MaxOutputBytes, int? TimeoutSeconds,
    string? Provider = null, string? Tier = null);

public sealed record TraceEventData(
    string Stage, string Outcome, string? ReasonCode = null,
    Guid? AttemptId = null, long? LlmCallId = null, long? PendingRecordId = null,
    long? RelatedSourceMessageId = null, long? ActorId = null, string? Text = null,
    bool? Sent = null, IReadOnlyList<TraceModelMessage>? Messages = null,
    TraceModelOptions? Options = null, int? PartIndex = null, int? PartCount = null,
    int? TelegramMessageId = null, int? EventCount = null, int? ProblemCount = null,
    string? Operation = null);

public interface ITraceSession
{
    bool Enabled { get; }
    Guid? TraceId { get; }
    Task StartAsync(TraceStart start, CancellationToken cancellationToken);
    Task RecordAsync(TraceEventData data, CancellationToken cancellationToken);
    ITelegramClient Wrap(ITelegramClient inner);
}

public sealed class NullTraceSession : ITraceSession
{
    public static NullTraceSession Instance { get; } = new();
    public bool Enabled => false;
    public Guid? TraceId => null;
    public Task StartAsync(TraceStart start, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RecordAsync(TraceEventData data, CancellationToken cancellationToken) => Task.CompletedTask;
    public ITelegramClient Wrap(ITelegramClient inner) => inner;
}
