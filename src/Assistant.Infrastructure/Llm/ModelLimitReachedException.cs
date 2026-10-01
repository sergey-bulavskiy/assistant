namespace Assistant.Infrastructure.Llm;

/// <summary>Whether a ModelLimitReachedException affects only the one model entry that was called, or
/// every catalog entry for that provider (spec §8.7: session/weekly/spend/usage limits are
/// account-wide; a model-named limit like "Opus limit" is not).</summary>
public enum LlmLimitScope { Model, Provider }

/// <summary>Thrown by a provider's IChatClient when the model's quota/usage/rate limit is reached.
/// The gateway marks the affected entry/entries (per <see cref="Scope"/>) unavailable until
/// <see cref="RetryAt"/> (or, if the provider gave no time, for LLM_MODEL_COOLDOWN_MINUTES) and tries
/// the next candidate model.</summary>
public class ModelLimitReachedException : Exception
{
    public DateTimeOffset? RetryAt { get; }

    public LlmLimitScope Scope { get; }

    public ModelLimitReachedException(string message, LlmLimitScope scope, DateTimeOffset? retryAt = null) : base(message)
    {
        Scope = scope;
        RetryAt = retryAt;
    }
}
