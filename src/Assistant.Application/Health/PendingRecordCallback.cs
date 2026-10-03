using System.Globalization;

namespace Assistant.Application.Health;

/// <summary>Callback data of the Да/Нет buttons: "rec_yes:{id}" / "rec_no:{id}" with the
/// pending_records id (at most 27 bytes, under Telegram's 64-byte limit).</summary>
public static class PendingRecordCallback
{
    public const string YesPrefix = "rec_yes:";
    public const string NoPrefix = "rec_no:";

    public static string Format(bool accept, long id) =>
        (accept ? YesPrefix : NoPrefix) + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>False for anything that is not exactly a prefix and a positive id.</summary>
    public static bool TryParse(string data, out bool accept, out long id)
    {
        id = 0;
        accept = data.StartsWith(YesPrefix, StringComparison.Ordinal);
        string? rest = accept
            ? data[YesPrefix.Length..]
            : data.StartsWith(NoPrefix, StringComparison.Ordinal) ? data[NoPrefix.Length..] : null;
        return rest is not null && long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
