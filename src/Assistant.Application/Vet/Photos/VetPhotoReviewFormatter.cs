using System.Globalization;
using System.Text;

namespace Assistant.Application.Vet.Photos;

public enum VetPhotoReviewSection { Clear, Exceptions, Duplicates, Excluded, Failed }
public sealed record VetPhotoReviewRow(int ItemNumber, VetPhotoReviewSection Section,
    VetPhotoEffectiveReading? Effective, string Description, string? SamePlaceSourceLink = null)
{
    public IReadOnlyList<string> EvidenceBlocks { get; init; } = Array.Empty<string>();
}
public sealed record VetPhotoPreviewResult(IReadOnlyList<string> Pages, string? FailureReason)
{
    public bool Success => FailureReason is null && Pages.Count > 0;
}

public static class VetPhotoReviewFormatter
{
    public const int MaxPageChars = 3500;
    public const int MaxReviewPages = 64;
    public const string RefusalReason = "invalid_preview_content";
    private const int MaxBlocks = 10000;
    private static readonly VetPhotoReviewSection[] Sections = [VetPhotoReviewSection.Clear,
        VetPhotoReviewSection.Exceptions, VetPhotoReviewSection.Duplicates,
        VetPhotoReviewSection.Excluded, VetPhotoReviewSection.Failed];

    public static VetPhotoPreviewResult Format(string heading, IReadOnlyList<string> assumptions,
        IReadOnlyList<VetPhotoReviewRow> rows, string footer)
    {
        if (!ValidText(heading) || !ValidText(footer) || assumptions is null || rows is null
            || assumptions.Count > MaxBlocks || rows.Count > 50
            || assumptions.Any(a => !ValidText(a)) || rows.Any(r => !ValidRow(r))
            || rows.Sum(r => r.EvidenceBlocks.Count) > MaxBlocks
            || rows.Select(r => r.ItemNumber).Distinct().Count() != rows.Count)
            return Refuse();
        var blocks = new List<string>();
        foreach (var section in Sections)
        {
            var selected = rows.Where(r => r.Section == section);
            selected = section == VetPhotoReviewSection.Clear
                ? selected.OrderBy(r => r.Effective!.OccurredAt).ThenBy(r => r.ItemNumber)
                : selected.OrderBy(r => r.ItemNumber);
            var first = true;
            foreach (var row in selected)
            {
                var block = Render(row);
                if (first) block = SectionName(section) + "\n" + block;
                if (!ValidText(block)) return Refuse();
                blocks.Add(block);
                blocks.AddRange(row.EvidenceBlocks);
                first = false;
            }
        }
        foreach (var assumption in assumptions)
        {
            var block = "Допущение: " + assumption;
            if (!ValidText(block)) return Refuse();
            blocks.Add(block);
        }
        return Pack(heading, blocks, footer);
    }

    // Links are preselected by the exact-place caller. This pure formatter grants no access.
    public static VetPhotoPreviewResult FormatBlocks(string heading, IReadOnlyList<string> fullBlocks, string footer)
    {
        if (!ValidText(heading) || !ValidText(footer) || fullBlocks is null
            || fullBlocks.Count > MaxBlocks || fullBlocks.Any(b => !ValidText(b))) return Refuse();
        return Pack(heading, fullBlocks, footer);
    }

    private static VetPhotoPreviewResult Pack(string heading, IReadOnlyList<string> blocks, string footer)
    {
        var pages = new List<string>();
        var current = new StringBuilder(heading);
        foreach (var block in blocks.Append(footer))
        {
            if (current.Length + 2 + block.Length > MaxPageChars)
            {
                pages.Add(current.ToString());
                if (pages.Count >= MaxReviewPages) return Refuse();
                current.Clear();
            }
            if (current.Length != 0) current.Append("\n\n");
            current.Append(block);
        }
        if (current.Length != 0) pages.Add(current.ToString());
        return new(Array.AsReadOnly(pages.ToArray()), null);
    }

    private static string Render(VetPhotoReviewRow row)
    {
        var text = new StringBuilder("#").Append(row.ItemNumber.ToString(CultureInfo.InvariantCulture));
        if (row.Effective is { } reading)
        {
            text.Append(": ").Append(reading.Value.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reading.Unit)
                .Append("\nВремя: ").Append(reading.LocalTime).Append("; зона: ").Append(reading.TimeZoneSnapshot)
                .Append("\nUTC: ").Append(reading.OccurredAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                .Append("\nОснование: значение=").Append(EvidenceLabel(reading.ValueEvidence)).Append("; единица=").Append(EvidenceLabel(reading.UnitEvidence))
                .Append("; время=").Append(EvidenceLabel(reading.TimeEvidence))
                .Append("; профильные допущения=").Append(reading.UsesProfileDefaults ? "да" : "нет");
        }
        text.Append('\n').Append(row.Description);
        if (row.SamePlaceSourceLink is { } link) text.Append("\nИсточник: ").Append(link);
        return text.ToString();
    }

    private static bool ValidRow(VetPhotoReviewRow row)
    {
        if (row is null || row.ItemNumber <= 0 || !Sections.Contains(row.Section) || !ValidText(row.Description)
            || row.EvidenceBlocks is null || row.EvidenceBlocks.Count > MaxBlocks || row.EvidenceBlocks.Any(b => !ValidText(b))
            || row.Section == VetPhotoReviewSection.Clear && row.Effective is null)
            return false;
        if (row.SamePlaceSourceLink is { } link && (!ValidText(link)
            || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "t.me"
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            return false;
        if (row.Effective is { } reading && (reading.Value <= 0 || reading.Unit != "mmol/L"
            || !ValidField(reading.LocalTime) || !ValidField(reading.TimeZoneSnapshot)
            || reading.ValueEvidence is not ("image" or "human_correction")
            || reading.UnitEvidence is not ("image" or "caption" or "batch_default" or "human_correction")
            || reading.TimeEvidence is not ("image_or_caption" or "batch_year" or "human_correction")))
            return false;
        return true;
    }

    private static bool ValidField(string value) => value is { Length: <= 256 } && ValidText(value);
    private static bool ValidText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxPageChars || value.Contains('\0')) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static string SectionName(VetPhotoReviewSection section) => section switch
    {
        VetPhotoReviewSection.Clear => "Готовые показания",
        VetPhotoReviewSection.Exceptions => "Требуют уточнения",
        VetPhotoReviewSection.Duplicates => "Возможные повторы",
        VetPhotoReviewSection.Excluded => "Исключены",
        VetPhotoReviewSection.Failed => "Ошибки обработки",
        _ => throw new InvalidOperationException("Invalid photo review section.")
    };
    private static string EvidenceLabel(string evidence) => evidence switch
    {
        "image" => "по фото",
        "caption" => "по подписи",
        "human_correction" => "исправление пользователя",
        "batch_default" => "подтверждённые допущения",
        "image_or_caption" => "фото или подпись",
        "batch_year" => "подтверждённый год",
        _ => throw new InvalidOperationException("Invalid photo review evidence.")
    };
    private static VetPhotoPreviewResult Refuse() => new(Array.Empty<string>(), RefusalReason);
}
