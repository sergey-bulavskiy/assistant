namespace Assistant.IntegrationTests.Host;

/// <summary>Review should-fix #5: <see cref="LlmWiringTests"/> sets process-wide environment
/// variables (LLM_*/CLAUDE_CODE_OAUTH_TOKEN) for the brief window between building each
/// <see cref="AssistantWebApplicationFactory"/> and that host finishing its eager config read,
/// restoring them in a `finally` right after. Every other test class that also builds an
/// AssistantWebApplicationFactory (today, only <see cref="BotPollingCoordinatorTests"/>) must never
/// run concurrently with that window, or it could observe a stray value during its own eager read.
/// Sharing this collection with <see cref="DisableParallelization"/> set serializes all of them
/// against each other (xUnit already runs test methods of one class sequentially; this instead
/// stops different classes/collections from racing each other).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class HostFactoryCollection
{
    public const string Name = "Host factory (env-sensitive)";
}
