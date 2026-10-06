namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceCaptureState
{
    private int _paused;
    public bool Paused => Volatile.Read(ref _paused) != 0;
    public void Pause() => Volatile.Write(ref _paused, 1);
    public void Resume() => Volatile.Write(ref _paused, 0);
}
