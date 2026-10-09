using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Application.Telegram;

namespace Assistant.Application.Reminders;

public static class ReminderParser
{
    public const string Help = "Напоминания: /remind in 10m текст; /remind at 2026-10-09T14:30 текст; /remind daily 14:30 текст. /reminders — список; /reminder_settings +03:00 22:00 08:00 — фиксированное смещение UTC и тихие часы, без перехода на летнее время.";
    private static Match Match(string input, string pattern) => Regex.Match(input, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool LooksConversational(string text)
    {
        var value = text.TrimStart();
        static bool StartsWord(string value, string word) => value.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            && (value.Length == word.Length || char.IsWhiteSpace(value[word.Length]));
        return StartsWord(value, "напомни") || StartsWord(value, "напоминай");
    }
    public static ReminderParseResult Parse(string text, string username, DateTimeOffset now, ReminderPreferences preferences)
    {
        if (text.Length > 2048) return new(true, "invalid", Error: Help);
        try { return ParseCore(text, username, now, preferences); }
        catch (RegexMatchTimeoutException) { return new(true, "invalid", Error: Help); }
        catch (ArgumentOutOfRangeException) { return new(true, "invalid", Error: Help); }
    }
    private static ReminderParseResult ParseCore(string text, string username, DateTimeOffset now, ReminderPreferences preferences)
    {
        var command = CommandParser.Parse(text, username);
        var input = text.Trim();
        if (command == "reminders") return new(true, "list");
        if (command == "reminder_settings")
        {
            var args = CommandParser.ParseArgs(text);
            if (args == null) return new(true, "settings");
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !TryOffset(parts[0], out var offset)
                || !TryMinute(parts[1], out var start) || !TryMinute(parts[2], out var end) || start == end)
                return new(true, "invalid", Error: Help);
            return new(true, "set_settings", Preferences: new(offset, start, end));
        }
        if (command == "remind") input = CommandParser.ParseArgs(text) ?? "";
        else if (input.StartsWith('/') || !LooksConversational(input)) return new(false, "none");
        var relative = Match(input, @"^in (?<n>[0-9]{1,6})(?<unit>[mhd])\s+(?<text>.+)$");
        var russianRelative = Match(input, @"^напомни через (?<n>[0-9]{1,6}) (?<unit>минут(?:у|ы)?|час(?:а|ов)?|д(?:ень|ня|ней))\s+(?<text>.+)$");
        var absolute = Match(input, @"^at (?<date>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2})\s+(?<text>.+)$");
        var russianAbsolute = Match(input, @"^напомни (?<date>[0-9]{2}\.[0-9]{2}\.[0-9]{4}) в (?<time>[0-9]{2}:[0-9]{2})\s+(?<text>.+)$");
        var daily = Match(input, @"^(?:daily|напоминай каждый день в) (?<time>[0-9]{2}:[0-9]{2})\s+(?<text>.+)$");
        DateTimeOffset due;
        int? dailyMinute = null;
        string task;
        if (relative.Success || russianRelative.Success)
        {
            var match = relative.Success ? relative : russianRelative;
            var number = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var multiplier = unit.StartsWith('m') || unit.StartsWith("мин", StringComparison.Ordinal) ? 1
                : unit.StartsWith('h') || unit.StartsWith("час", StringComparison.Ordinal) ? 60 : 1440;
            var minutes = (long)number * multiplier;
            if (minutes is <= 0 or > 527040) return new(true, "invalid", Error: Help);
            due = now.AddMinutes(minutes);
            task = match.Groups["text"].Value.Trim();
        }
        else if (absolute.Success || russianAbsolute.Success)
        {
            var match = absolute.Success ? absolute : russianAbsolute;
            var value = absolute.Success ? match.Groups["date"].Value
                : match.Groups["date"].Value + " " + match.Groups["time"].Value;
            var format = absolute.Success ? "yyyy-MM-dd'T'HH:mm" : "dd.MM.yyyy HH:mm";
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return new(true, "invalid", Error: Help);
            due = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified),
                TimeSpan.FromMinutes(preferences.OffsetMinutes)).ToUniversalTime();
            task = match.Groups["text"].Value.Trim();
        }
        else if (daily.Success && TryMinute(daily.Groups["time"].Value, out var minute))
        {
            dailyMinute = minute;
            due = ReminderTimePolicy.NextDaily(now, minute, preferences.OffsetMinutes);
            task = daily.Groups["text"].Value.Trim();
        }
        else return new(true, "invalid", Error: Help);
        if (task.Length is < 1 or > 500 || task.Any(char.IsControl) || due <= now || due > now.AddDays(366))
            return new(true, "invalid", Error: Help);
        return new(true, "create", new(task, due, dailyMinute, preferences.OffsetMinutes));
    }
    public static bool TryMinute(string value, out int minute)
    {
        minute = 0;
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
        minute = time.Hour * 60 + time.Minute;
        return true;
    }
    public static bool TryOffset(string value, out int minutes)
    {
        minutes = 0;
        var match = Match(value, @"^(?<sign>[+-])(?<hour>[0-9]{2}):(?<minute>[0-9]{2})$");
        if (!match.Success) return false;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        if (minute >= 60) return false;
        minutes = (hour * 60 + minute) * (match.Groups["sign"].Value == "-" ? -1 : 1);
        return minutes is >= -720 and <= 840;
    }
}
