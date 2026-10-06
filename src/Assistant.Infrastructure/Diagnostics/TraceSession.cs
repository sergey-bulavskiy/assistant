using Assistant.Application.Diagnostics;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceSession : ITraceSession
{
    private readonly DebugTraceWriter _writer;
    private readonly TraceOptions _options;
    private readonly TraceCaptureState _state;
    private readonly ILogger<TraceSession> _logger;
    private long? _familyId;
    private Guid? _traceId;

    public TraceSession(DebugTraceWriter writer, TraceOptions options, TraceCaptureState state,
        ILogger<TraceSession> logger)
    {
        _writer = writer;
        _options = options;
        _state = state;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled && !_state.Paused && _traceId is not null;
    public Guid? TraceId => Enabled ? _traceId : null;

    public async Task StartAsync(TraceStart start, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _state.Paused || _traceId is not null) return;
        try
        {
            if (await _writer.StartAsync(start, cancellationToken) is { } traceId)
            {
                _familyId = start.FamilyId;
                _traceId = traceId;
            }
        }
        catch (Exception ex)
        {
            _state.Pause();
            _logger.LogWarning("Debug trace capture paused after a storage failure: {FailureType}", ex.GetType().Name);
        }
    }

    public async Task RecordAsync(TraceEventData data, CancellationToken cancellationToken)
    {
        if (!Enabled || _familyId is null || _traceId is null) return;
        try
        {
            await _writer.AppendAsync(_traceId.Value, _familyId.Value, data, cancellationToken);
        }
        catch (Exception ex)
        {
            _state.Pause();
            _logger.LogWarning("Debug trace capture paused after a storage failure: {FailureType}", ex.GetType().Name);
        }
    }

    public ITelegramClient Wrap(ITelegramClient inner) => Enabled
        ? new TracingTelegramClient(this, inner)
        : inner;
}
