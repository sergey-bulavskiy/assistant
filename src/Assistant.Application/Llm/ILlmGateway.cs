namespace Assistant.Application.Llm;

public enum LlmMessageRole { User, Assistant }

/// <summary>Author, when given, is the display name the transcript sent to the CLI shows for this
/// turn (spec §8.4's `<msg author="...">`) -- used in groups where several people talk (spec 2.4);
/// null in private chats where there's nothing to disambiguate.</summary>
public record LlmMessage(LlmMessageRole Role, string Text, string? Author = null);

public record LlmRequest(
    long FamilyId,
    long BotId,
    string Tier,
    string? PreferredModel,
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages);

/// <summary>BudgetExhausted: the platform money budget leaves no callable model; its RetryAt is the
/// reset of the binding budget period (next UTC day or month).</summary>
public enum LlmRefusalReason { NotConfigured, RateLimited, DailyCapReached, AllModelsUnavailable, Failed, BudgetExhausted }

public record LlmResult(bool IsAnswer, string? Text, string? ModelName, LlmRefusalReason? RefusalReason, DateTimeOffset? RetryAt)
{
    public static LlmResult Answered(string text, string modelName) => new(true, text, modelName, null, null);

    public static LlmResult Refused(LlmRefusalReason reason, DateTimeOffset? retryAt = null) => new(false, null, null, reason, retryAt);
}

public record ModelStatus(string Name, bool IsAvailable, DateTimeOffset? RetryAt);

public interface ILlmGateway
{
    bool IsEnabled { get; }

    IReadOnlyList<ModelStatus> DescribeModels();

    bool IsKnownModel(string name);

    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
