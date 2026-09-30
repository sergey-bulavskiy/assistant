namespace Assistant.Infrastructure.Llm;

/// <summary>In-memory, process-lifetime tracking of which catalog model names are currently
/// unavailable (spec 3.1: "Lost on restart by design"). Case-insensitive by model name.</summary>
public interface IModelAvailability
{
    bool IsAvailable(string modelName);

    DateTimeOffset? RetryAt(string modelName);

    void MarkUnavailable(string modelName, DateTimeOffset until);

    /// <summary>Clears any unavailability mark (spec §8.10: the CLI-install hosted service, Task 10,
    /// uses this once the pinned CLI version is confirmed installed). A no-op otherwise.</summary>
    void MarkAvailable(string modelName);
}
