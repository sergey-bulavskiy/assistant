using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

[CollectionDefinition(nameof(ProcessWideEnvironmentCollection), DisableParallelization = true)]
public class ProcessWideEnvironmentCollection
{
}

/// <summary>Mutates THIS test process's own environment variables to simulate the real hosting
/// process leaking secrets into its environment (mirrors ClaudeCliChatClientTests's equivalent
/// check at the options-allowlist level; this one proves it at the real-process level). Runs in a
/// non-parallel collection since <c>Environment.SetEnvironmentVariable</c> here is process-wide
/// state shared with every other test.</summary>
[Collection(nameof(ProcessWideEnvironmentCollection))]
public class ProcessRunnerEnvironmentIsolationTests
{
    [UnixOnlyFact]
    public async Task The_childs_environment_never_contains_a_variable_the_parent_process_has_but_the_request_did_not_list()
    {
        Environment.SetEnvironmentVariable("ASSISTANT_TEST_LEAK_VAR", "should-not-reach-child");
        try
        {
            var runner = new ProcessRunner();
            var environment = new Dictionary<string, string> { ["ASSISTANT_TEST_VAR"] = "visible-value" };
            var request = new ProcessRunRequest(
                "/bin/sh", new[] { "-c", "env" }, environment, Path.GetTempPath(), string.Empty, TimeSpan.FromSeconds(10));

            var result = await runner.RunAsync(request, CancellationToken.None);

            result.StandardOutput.ShouldContain("ASSISTANT_TEST_VAR=visible-value");
            result.StandardOutput.ShouldNotContain("ASSISTANT_TEST_LEAK_VAR");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_TEST_LEAK_VAR", null);
        }
    }
}
