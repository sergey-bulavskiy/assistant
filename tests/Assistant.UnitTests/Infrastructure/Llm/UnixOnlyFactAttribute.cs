using System.Runtime.InteropServices;

namespace Assistant.UnitTests.Infrastructure.Llm;

/// <summary>A [Fact] that runs a real child process (`/bin/sh`, `sleep`, ...) and so only makes sense
/// on Linux/macOS -- CI runs on Linux, but a dev machine may be Windows. Skipped there instead of
/// failing on a missing/incompatible shell.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Runs a real Unix process (/bin/sh, sleep); skipped on Windows.";
        }
    }
}
