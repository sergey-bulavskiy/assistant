using Assistant.Infrastructure.Llm.ClaudeCli;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Llm;

/// <summary>Spec §8.10 (licence decision C9): the Claude Code CLI is proprietary and this repo is
/// public, so it is never baked into the image. This service runs once at startup, in the
/// background, and installs the pinned version into ClaudeCliOptions.HomeDirectory
/// ($CLAUDE_HOME -- a dedicated volume, Task 10 Dockerfile/compose) if it isn't already there at the
/// right version. Every claude-cli catalog entry is marked unavailable (Task 9 does this
/// synchronously before this service's StartAsync even runs, at DI composition time -- see
/// InfrastructureServiceCollectionExtensions) until the check/install here succeeds, so callers get
/// AllModelsUnavailable rather than a confusing process-launch failure. Never blocks
/// Program.cs's own startup and never throws out of the app -- an install failure logs exactly one
/// Error (exception type / stderr never included) and is retried only on the next app start, never
/// on an in-process timer.</summary>
public class ClaudeCliInstallerHostedService : IHostedService
{
    private readonly IProcessRunner _runner;
    private readonly ClaudeCliOptions _options;
    private readonly IModelAvailability _availability;
    private readonly IReadOnlyList<string> _claudeCliModelNames;
    private readonly ILogger<ClaudeCliInstallerHostedService> _logger;

    public ClaudeCliInstallerHostedService(
        IProcessRunner runner,
        ClaudeCliOptions options,
        IModelAvailability availability,
        IReadOnlyList<string> claudeCliModelNames,
        ILogger<ClaudeCliInstallerHostedService> logger)
    {
        _runner = runner;
        _options = options;
        _availability = availability;
        _claudeCliModelNames = claudeCliModelNames;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsAlreadyAtPinnedVersionAsync(cancellationToken))
            {
                var installResult = await RunInstallerAsync(cancellationToken);
                if (installResult.ExitCode != 0)
                {
                    _logger.LogError("claude-cli install failed: exit code {ExitCode}", installResult.ExitCode);
                    return;
                }
            }

            foreach (var modelName in _claudeCliModelNames)
            {
                _availability.MarkAvailable(modelName);
            }
        }
        catch (Exception ex)
        {
            // Never let an install failure crash the app (spec §8.10) -- only exception type, never
            // stdout/stderr, which may echo network/proxy details best not logged verbatim anyway.
            _logger.LogError("claude-cli install failed: {ExceptionType}", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> IsAlreadyAtPinnedVersionAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new ProcessRunRequest(
                _options.ExecutablePath ?? Path.Combine(_options.HomeDirectory, ".local", "bin", "claude"),
                new[] { "--version" },
                BuildEnvironment(),
                _options.HomeDirectory,
                StandardInput: string.Empty,
                Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken);

        return result.ExitCode == 0 && result.StandardOutput.Contains(_options.PinnedVersion, StringComparison.Ordinal);
    }

    private async Task<ProcessRunResult> RunInstallerAsync(CancellationToken cancellationToken)
    {
        // Anthropic's official native installer (Verified facts #6): pipes the install script through
        // bash with the pinned version as its one argument. HOME=$CLAUDE_HOME for this process only,
        // so the CLI lands under the dedicated volume, never the app's own HOME.
        return await _runner.RunAsync(
            new ProcessRunRequest(
                "bash",
                new[] { "-c", $"curl -fsSL https://claude.ai/install.sh | bash -s {_options.PinnedVersion}" },
                BuildEnvironment(),
                _options.HomeDirectory,
                StandardInput: string.Empty,
                Timeout: TimeSpan.FromMinutes(2)),
            cancellationToken);
    }

    private Dictionary<string, string> BuildEnvironment() => new()
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        ["HOME"] = _options.HomeDirectory
    };
}
