namespace Assistant.Infrastructure.Llm;

/// <summary>In-memory, process-lifetime tracking of which catalog model names are currently
/// unavailable (spec 3.1: "Lost on restart by design"). Case-insensitive by model name.</summary>
public interface IModelAvailability
{
    bool IsAvailable(string modelName);

    DateTimeOffset? RetryAt(string modelName);

    void MarkUnavailable(string modelName, DateTimeOffset until);

    /// <summary>Clears any unavailability mark. A no-op if the model was never marked
    /// unavailable.</summary>
    void MarkAvailable(string modelName);
}
