using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceCleanupService : BackgroundService
{
    private readonly DebugTraceWriter _writer;
    private readonly TraceOptions _options;
    private readonly TraceCaptureState _state;
    private readonly ILogger<TraceCleanupService> _logger;

    public TraceCleanupService(DebugTraceWriter writer, TraceOptions options, TraceCaptureState state,
        ILogger<TraceCleanupService> logger)
    {
        _writer = writer;
        _options = options;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ValidationWarning is not null)
            _logger.LogWarning("{Warning}", _options.ValidationWarning);
        if (_options.Enabled)
            _logger.LogWarning("Private debug trace capture is enabled. Redaction cannot detect secrets typed into ordinary message content.");

        await CleanupOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(TraceOptions.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CleanupOnceAsync(stoppingToken);
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        try { await _writer.CleanupAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state.Pause();
            _logger.LogWarning("Debug trace cleanup failed: {FailureType}", ex.GetType().Name);
        }
    }
}
