using System.Text.Json.Nodes;
using System.Text.Json;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.CodexCli;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.AI;

namespace Assistant.UnitTests.Infrastructure.Llm;

public sealed class CodexCliChatClientTests
{
    private const string Success = """
        {"type":"thread.started","thread_id":"synthetic"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"type":"reasoning","text":"private synthetic reasoning"}}
        {"type":"item.completed","item":{"type":"agent_message","text":"synthetic answer"}}
        {"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":20,"output_tokens":10}}
        """;

    private static (CodexCliChatClient Client, FakeProcessRunner Runner) Create(
        string output = Success, int exit = 0, string error = "", bool timeout = false,
        Action<ProcessRunRequest>? inspect = null, string version = "codex-cli 0.160.1", string final = "synthetic answer",
        bool images = false, bool imageCatalog = true)
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments[0] == "--version") return new(0, version, "", false);
                if (request.Arguments[0] == "debug") return new(0,
                    "{\"models\":[{\"slug\":\"gpt-6.1-sol\",\"input_modalities\":[" + (imageCatalog ? "\"text\",\"image\"" : "\"text\"") + "],\"shell_type\":\"unified_exec\",\"apply_patch_tool_type\":\"freeform\",\"experimental_supported_tools\":[\"clock\"]}]}", "", false);
                inspect?.Invoke(request);
                var finalPath = request.Arguments[request.Arguments.IndexOf("--output-last-message") + 1];
                File.WriteAllText(finalPath, final);
                return new(exit, output, error, timeout);
            }
        };
        return (CreateClient(runner, images), runner);
    }

    private static CodexCliChatClient CreateClient(IProcessRunner runner, bool images = false) => new(runner, new CodexCliOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "synthetic-codex"),
            HomeDirectory = Path.Combine(Path.GetTempPath(), "synthetic-auth"),
            MaxOutputTokens = 1000, CallTimeoutSeconds = 30, ImageInputEnabled = images
        });

    private static Task<ChatResponse> Call(CodexCliChatClient client, CancellationToken token = default) =>
        client.GetResponseAsync([new(ChatRole.System, "synthetic role"), new(ChatRole.User, "synthetic question")],
            new ChatOptions { ModelId = "gpt-6.1-sol", MaxOutputTokens = 1000 }, token);

    [Fact]
    public async Task ReturnsOnlyFinalAnswerUsageAndZeroSubscriptionCharge()
    {
        var (client, _) = Create();
        var response = await Call(client);
        response.Text.ShouldBe("synthetic answer");
        response.ModelId.ShouldBe("gpt-6.1-sol");
        response.Usage!.InputTokenCount.ShouldBe(120);
        response.Usage.CachedInputTokenCount.ShouldBe(20);
        response.Usage.TotalTokenCount.ShouldBe(130);
        response.AdditionalProperties!.Values.ShouldContain(0m);
    }

    private static Task<ChatResponse> ImageCall(CodexCliChatClient client, byte[]? data = null) =>
        client.GetResponseAsync([new(ChatRole.User,
            [new TextContent("synthetic image caption"),
             new DataContent(data ?? new byte[] {137,80,78,71,13,10,26,10}, "image/png")])],
            new ChatOptions { ModelId = "gpt-6.1-sol" });

    [Fact]
    public async Task NativeImageIsExactPrivateFileWithDisabledToolsAndCleanup()
    {
        string? directory = null;
        var data = new byte[] {137,80,78,71,13,10,26,10,42};
        var (client, _) = Create(images: true, inspect: request =>
        {
            directory = request.WorkingDirectory;
            var imagePath = request.Arguments[request.Arguments.IndexOf("--image") + 1];
            Path.GetDirectoryName(imagePath).ShouldBe(directory);
            File.ReadAllBytes(imagePath).ShouldBe(data);
            request.StandardInput.ShouldNotContain(imagePath);
            request.StandardInput.ShouldNotContain(Convert.ToBase64String(data));
            request.Arguments[request.Arguments.IndexOf("view_image") - 1].ShouldBe("--disable");
            request.Arguments[request.Arguments.IndexOf("shell_tool") - 1].ShouldBe("--disable");
            request.Environment.Keys.ShouldNotContain("OPENAI_API_KEY");
            JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "models.json")))!["models"]![0]!["input_modalities"]!
                .AsArray().Select(x => x!.GetValue<string>()).ShouldContain("image");
        });
        var response = await ImageCall(client, data);
        response.Text.ShouldBe("synthetic answer");
        response.Usage!.InputTokenCount.ShouldBe(120);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task ExplicitlyDisabledImageRejectsBeforeAnyProcess()
    {
        var (client, runner) = Create();
        (await Should.ThrowAsync<InvalidOperationException>(() => ImageCall(client))).Message.ShouldContain("unsupported-image");
        runner.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ImageWithoutNativeCatalogCapabilityDoesNotExecuteAndCleansDirectory()
    {
        var (client, runner) = Create(images: true, imageCatalog: false);
        (await Should.ThrowAsync<InvalidOperationException>(() => ImageCall(client))).Message.ShouldContain("image");
        runner.LastRequest!.Arguments[0].ShouldBe("debug");
        Directory.Exists(runner.LastRequest.WorkingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task IncorrectImageSignatureRejectsBeforeAnyProcess()
    {
        var (client, runner) = Create(images: true);
        (await Should.ThrowAsync<InvalidOperationException>(() => ImageCall(client, [1,2,3]))).Message.ShouldContain("unsupported-image");
        runner.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task RemovesCapabilitiesInheritedContextAndSecretsAndCleansTemporaryFiles()
    {
        string? directory = null;
        var (client, _) = Create(inspect: request =>
        {
            directory = request.WorkingDirectory;
            request.Environment.Keys.ShouldNotContain("OPENAI_API_KEY");
            request.Environment.Keys.ShouldNotContain("ORCA_AGENT_HOOK_TOKEN");
            request.Environment.Keys.ShouldNotContain("TELEGRAM_MANAGER_BOT_TOKEN");
            request.Environment["HOME"].ShouldBe(request.WorkingDirectory);
            request.Environment["CODEX_HOME"].ShouldNotBe(request.WorkingDirectory);
            request.Arguments.ShouldContain("--ignore-user-config");
            request.Arguments.ShouldContain("--ignore-rules");
            request.Arguments.ShouldContain("--ephemeral");
            request.Arguments.ShouldContain("forced_login_method=\"chatgpt\"");
            request.Arguments.ShouldContain("model_provider=\"openai\"");
            request.Arguments.ShouldContain("tools.update_plan.enabled=false");
            request.Arguments.ShouldContain("skills.include_instructions=false");
            request.Arguments.ShouldContain("developer_instructions=\"\"");
            var model = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "models.json")))!["models"]![0]!;
            model["shell_type"]!.GetValue<string>().ShouldBe("disabled");
            model["apply_patch_tool_type"].ShouldBeNull();
            model["tool_mode"]!.GetValue<string>().ShouldBe("direct");
            model["experimental_supported_tools"]!.AsArray().ShouldBeEmpty();
            model["model_messages"].ShouldBeNull();
            File.ReadAllText(Path.Combine(directory, "instructions.txt")).ShouldContain("synthetic role");
            request.StandardInput.ShouldContain("synthetic question");
            request.StandardInput.ShouldNotContain("synthetic role");
        });
        await Call(client);
        directory.ShouldNotBeNull();
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public async Task RefusesUnverifiedBinaryBeforeModelExecution()
    {
        var (client, runner) = Create(version: "codex-cli 0.160.2");
        (await Should.ThrowAsync<InvalidOperationException>(() => Call(client))).Message.ShouldContain("version");
        runner.LastRequest!.Arguments.ShouldBe(["--version"]);
    }

    [Theory]
    [InlineData("not json", "invalid-json")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}", "unexpected-tool-output")]
    [InlineData("{\"type\":\"turn.started\"}", "incomplete-result")]
    public async Task RejectsMalformedToolAndIncompleteResults(string output, string category)
    {
        var (client, _) = Create(output);
        (await Should.ThrowAsync<InvalidOperationException>(() => Call(client))).Message.ShouldContain(category);
    }

    [Fact]
    public async Task AuthErrorIsSanitizedAndDistinctFromQuota()
    {
        var (client, _) = Create("{\"type\":\"turn.failed\",\"error\":{\"message\":\"authentication failed synthetic-secret\"}}", 1);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Call(client));
        error.Message.ShouldContain("authentication");
        error.Message.ShouldNotContain("synthetic-secret");
    }

    [Fact]
    public async Task QuotaDisablesTheSubscriptionProviderWithoutLeakingErrorText()
    {
        var (client, _) = Create("{\"type\":\"turn.failed\",\"error\":{\"message\":\"usage_limit_reached synthetic-secret\"}}", 1);
        var error = await Should.ThrowAsync<ModelLimitReachedException>(() => Call(client));
        error.Scope.ShouldBe(LlmLimitScope.Provider);
        error.Message.ShouldNotContain("synthetic-secret");
    }

    [Fact]
    public async Task TimeoutCleansTheRequestDirectory()
    {
        var (client, runner) = Create(timeout: true);
        await Should.ThrowAsync<TimeoutException>(() => Call(client));
        Directory.Exists(runner.LastRequest!.WorkingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task CancellationPropagatesAndCleansTheRequestDirectory()
    {
        var (_, setupRunner) = Create();
        var execStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new TokenAwareRunner(async (request, token) =>
        {
            if (request.Arguments[0] != "exec") return await setupRunner.RunAsync(request, token);
            execStarted.TrySetResult(request.WorkingDirectory);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("exec must be cancelled");
        });
        using var cancellation = new CancellationTokenSource();
        var call = Call(CreateClient(runner), cancellation.Token);
        var directory = await execStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Directory.Exists(directory).ShouldBeFalse();
    }

    private sealed class TokenAwareRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> handler) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    [Theory]
    [InlineData(8000, true)]
    [InlineData(8001, false)]
    public async Task AcceptsTheExactCharacterCeilingAndRejectsOneCharacterMore(int length, bool accepted)
    {
        var answer = new string('a', length);
        var output = Success.Replace("synthetic answer", answer);
        var (client, runner) = Create(output: output, final: answer);
        if (accepted) (await Call(client)).Text.ShouldBe(answer);
        else (await Should.ThrowAsync<InvalidOperationException>(() => Call(client))).Message.ShouldContain("final-output");
        Directory.Exists(runner.LastRequest!.WorkingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task RejectsAnOversizedUtf8FinalFileAndCleansItsDirectory()
    {
        var answer = new string('\u754c', 11000); // 33,000 bytes exceeds the 32,000-byte final-file cap.
        var output = Success.Replace("synthetic answer", JsonEncodedText.Encode(answer).ToString());
        var (client, runner) = Create(output: output, final: answer);
        (await Should.ThrowAsync<InvalidOperationException>(() => Call(client))).Message.ShouldContain("final-output");
        Directory.Exists(runner.LastRequest!.WorkingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task RefusesFinalFileThatDoesNotMatchTheCompletedAgentMessage()
    {
        var (client, _) = Create(inspect: request => { }, output: Success.Replace("synthetic answer", "different answer"));
        (await Should.ThrowAsync<InvalidOperationException>(() => Call(client))).Message.ShouldContain("final-output");
    }
}
