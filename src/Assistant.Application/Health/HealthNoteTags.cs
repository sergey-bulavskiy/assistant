using System.Text;

namespace Assistant.Application.Health;

public static class HealthNoteTags
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0) return false;
        var count = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune) || ++count > 32)
            {
                normalized = string.Empty;
                return false;
            }
        }
        return count > 0;
    }

    public static bool TryNormalizeMany(IReadOnlyList<string>? values, out string[] normalized)
    {
        var tags = new List<string>();
        if (values is null)
        {
            normalized = Array.Empty<string>();
            return false;
        }
        foreach (var value in values)
        {
            if (!TryNormalize(value, out var tag))
            {
                normalized = Array.Empty<string>();
                return false;
            }
            if (!tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
        }
        normalized = tags.ToArray();
        return normalized.Length is >= 1 and <= 5;
    }
}
