using Assistant.Application.Diagnostics;
using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public sealed class FakeTraceSession : ITraceSession
{
    public bool Enabled { get; private set; }
    public Guid? TraceId { get; private set; }
    public bool ThrowOnStart { get; set; }
    public bool ThrowOnRecord { get; set; }
    public List<TraceStart> Starts { get; } = new();
    public List<TraceEventData> Events { get; } = new();

    public Task StartAsync(TraceStart start, CancellationToken cancellationToken)
    {
        if (ThrowOnStart) throw new InvalidOperationException("simulated trace failure");
        Starts.Add(start);
        TraceId = start.TraceId;
        Enabled = true;
        return Task.CompletedTask;
    }

    public Task RecordAsync(TraceEventData data, CancellationToken cancellationToken)
    {
        if (ThrowOnRecord) throw new InvalidOperationException("simulated trace failure");
        Events.Add(data);
        return Task.CompletedTask;
    }

    public ITelegramClient Wrap(ITelegramClient inner) => Enabled ? new TracingTelegramClient(this, inner) : inner;
}
