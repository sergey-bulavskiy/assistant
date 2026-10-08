using System.Globalization;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Expectations;

public sealed record ExpectationCommand(string Kind, Guid? Id = null, string? EventType = null,
    int? DeadlineMinute = null, int? GraceMinutes = null, string? Error = null);

public static class ExpectationParser
{
    public const string Help = "/expect glucose daily 09:00 grace 30; /expectations; "
        + "/expect_edit <id> daily 09:00 grace 30; /expect_pause <id>; "
        + "/expect_resume <id>; /expect_cancel <id>. "
        + "Health: glucose — глюкоза, insulin — инсулин, meal — еда, symptom — симптом, "
        + "weight — вес, blood_pressure — давление. Vet: glucose — глюкоза, insulin — инсулин.";

    public static bool ValidType(string role, string type) => role switch
    {
        "health" => type is "glucose" or "insulin" or "meal" or "symptom" or "weight" or "blood_pressure",
        "vet" => type is "glucose" or "insulin",
        _ => false
    };

    public static ExpectationCommand? Parse(string text, string username, string role)
    {
        var command = CommandParser.Parse(text, username);
        if (command is not ("expect" or "expectations" or "expect_edit" or "expect_pause"
            or "expect_resume" or "expect_cancel")) return null;
        var invalid = new ExpectationCommand("invalid", Error: Help);
        if (role is not ("health" or "vet")) return invalid;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (command == "expectations") return words.Length == 1 ? new("list") : invalid;
        Guid? id = null;
        if (command != "expect")
        {
            if (words.Length < 2 || !Guid.TryParseExact(words[1], "N", out var parsed)) return invalid;
            id = parsed;
            if (command != "expect_edit") return words.Length == 2
                ? new(command[7..], id) : invalid;
        }
        if (words.Length != 6 || words[2] != "daily" || words[4] != "grace"
            || !TimeOnly.TryParseExact(words[3], "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)
            || !int.TryParse(words[5], NumberStyles.None, CultureInfo.InvariantCulture, out var grace)
            || grace is < 0 or > 180) return invalid;
        var minute = time.Hour * 60 + time.Minute;
        if (minute + grace >= 1440) return invalid;
        if (command == "expect" && !ValidType(role, words[1])) return invalid;
        return new(command == "expect" ? "create" : "edit", id,
            command == "expect" ? words[1] : null, minute, grace);
    }
}
