using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Assistant.Infrastructure.Diagnostics;

public sealed record TraceOptions(
    bool Enabled,
    int RetentionDays,
    int MaxDetailBytes,
    long MaxStorageBytes,
    int MaxEvents,
    string? ValidationWarning)
{
    public const int MaximumRetentionDays = 60;
    public const int MaximumDetailBytes = 262_144;
    public const long MaximumStorageBytes = 104_857_600;
    public const int MaximumEvents = 512;
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    public static TraceOptions Parse(IConfiguration configuration)
    {
        var valid = true;
        var rawEnabled = configuration["DEBUG_TRACES_ENABLED"];
        var enabled = string.Equals(rawEnabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(rawEnabled) && !bool.TryParse(rawEnabled, out _)) valid = false;

        var age = ReadBoundedInt(configuration, "DEBUG_TRACES_RETENTION_DAYS", 60, 1, MaximumRetentionDays, ref valid);
        var detail = ReadBoundedInt(configuration, "DEBUG_TRACES_MAX_DETAIL_BYTES", MaximumDetailBytes, 1024, MaximumDetailBytes, ref valid);
        var storage = ReadBoundedLong(configuration, "DEBUG_TRACES_MAX_STORAGE_BYTES", MaximumStorageBytes, 1_048_576, MaximumStorageBytes, ref valid);
        var events = ReadBoundedInt(configuration, "DEBUG_TRACES_MAX_EVENTS", 256, 16, MaximumEvents, ref valid);

        return new TraceOptions(enabled && valid, age, detail, storage, events,
            valid ? null : "Invalid debug trace settings; detailed capture is disabled.");
    }

    private static int ReadBoundedInt(IConfiguration config, string name, int fallback, int min, int max, ref bool valid)
    {
        var raw = config[name];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            return value;
        valid = false;
        return fallback;
    }

    private static long ReadBoundedLong(IConfiguration config, string name, long fallback, long min, long max, ref bool valid)
    {
        var raw = config[name];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            return value;
        valid = false;
        return fallback;
    }
}
