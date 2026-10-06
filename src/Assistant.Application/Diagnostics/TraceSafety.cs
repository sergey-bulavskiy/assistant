using Assistant.Application.Telegram;

namespace Assistant.Application.Diagnostics;

/// <summary>Diagnostics can never change the outcome of an authorized update.</summary>
public static class TraceSafety
{
    public static bool IsEnabled(ITraceSession trace)
    {
        try { return trace.Enabled; }
        catch { return false; }
    }

    public static async Task StartAsync(ITraceSession trace, TraceStart start)
    {
        try { await trace.StartAsync(start, CancellationToken.None); }
        catch { /* Diagnostic failure must not affect processing. */ }
    }

    public static async Task RecordAsync(ITraceSession trace, TraceEventData data)
    {
        if (!IsEnabled(trace)) return;
        try { await trace.RecordAsync(data, CancellationToken.None); }
        catch { /* Diagnostic failure must not affect processing. */ }
    }

    public static ITelegramClient Wrap(ITraceSession trace, ITelegramClient client)
    {
        try { return trace.Wrap(client); }
        catch { return client; }
    }
}
