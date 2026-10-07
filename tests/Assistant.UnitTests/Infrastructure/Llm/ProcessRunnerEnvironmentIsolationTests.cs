using System.Text;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

[CollectionDefinition(nameof(ProcessWideEnvironmentCollection), DisableParallelization = true)]
public class ProcessWideEnvironmentCollection
{
}

/// <summary>Mutates THIS test process's own environment variables to simulate the real hosting
/// process leaking secrets into its environment (mirrors ClaudeCliChatClientTests's equivalent
/// check at the options-allowlist level; this one proves it at the real-process level). Runs in a
/// non-parallel collection since environment variables and <c>Console.OutputEncoding</c> are
/// process-wide state shared with every other test.</summary>
[Collection(nameof(ProcessWideEnvironmentCollection))]
public class ProcessRunnerEnvironmentIsolationTests
{
    [Fact]
    public async Task Utf8_child_output_is_decoded_independently_of_parent_console_encoding()
    {
        var originalEncoding = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.Latin1;

            var environment = new Dictionary<string, string>();
            string fileName;
            string[] arguments;
            if (OperatingSystem.IsWindows())
            {
                fileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                    ?? Directory.GetParent(Environment.SystemDirectory)!.FullName;
                arguments = new[]
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::OpenStandardOutput().Write([byte[]](0xD0,0x96),0,2); "
                    + "[Console]::OpenStandardError().Write([byte[]](0xD1,0x8F),0,2)"
                };
            }
            else
            {
                fileName = "/bin/sh";
                arguments = new[] { "-c", "printf '\\320\\226'; printf '\\321\\217' >&2" };
            }

            var request = new ProcessRunRequest(fileName, arguments, environment, Path.GetTempPath(),
                string.Empty, TimeSpan.FromSeconds(10));
            var result = await new ProcessRunner().RunAsync(request, CancellationToken.None);

            result.ExitCode.ShouldBe(0);
            result.TimedOut.ShouldBeFalse();
            result.StandardOutput.ShouldBe("Ж");
            result.StandardError.ShouldBe("я");
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
        }
    }

    [UnixOnlyFact]
    public async Task The_childs_environment_never_contains_a_variable_the_parent_process_has_but_the_request_did_not_list()
    {
        var originalValue = Environment.GetEnvironmentVariable("ASSISTANT_TEST_LEAK_VAR");
        try
        {
            Environment.SetEnvironmentVariable("ASSISTANT_TEST_LEAK_VAR", "should-not-reach-child");
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
            Environment.SetEnvironmentVariable("ASSISTANT_TEST_LEAK_VAR", originalValue);
        }
    }
}
