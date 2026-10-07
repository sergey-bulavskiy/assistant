using System.Globalization;
using Assistant.Domain.Vet;

namespace Assistant.Application.Vet;

public static class VetEventValidation
{
    public static VetValidation Validate(VetCandidate c, VetProfile profile, VetAdmittedSource source, Guid resultId)
    {
        VetValidation Pending(string reason) => new(c, null, reason);
        if (c.Intent == "question_only") return new(c, null, null);
        if (c.Intent != "record") return Pending("Это уже измерено/введено или пока обсуждается?");
        if (c.EventType is not ("glucose" or "insulin")) return Pending("Поддерживаются записи глюкозы и введённого инсулина.");
        if (!VetInterpretationParser.TryPositiveDecimal(c.RawValue, out var value))
            return Pending("Укажите точное положительное значение без округления.");
        var unit = c.Unit ?? (c.EventType == "glucose" ? profile.GlucoseUnit : profile.InsulinUnit);
        if (unit != (c.EventType == "glucose" ? "mmol/L" : "U"))
            return Pending(c.EventType == "glucose" ? "Нужны значение и единица mmol/L; пересчёт mg/dL пока не поддерживается." : "Уточните дозу в U, не в миллилитрах.");
        if (c.Product?.Length is > 100) return Pending("Уточните название препарата.");
        if (!TryTime(c, profile, source.Source.SentAt, out var utc, out var local, out var zoneSnapshot, out var evidence, out var reason))
            return Pending(reason!);
        if (utc > source.Source.SentAt.AddMinutes(10))
            return Pending("Время находится в будущем: уточните, когда событие действительно произошло.");
        if (source.Source.SourceMessageDbId is not { } messageId) return Pending("Исходное сообщение ещё не сохранено.");
        return new(c, new(c.EventType, value, unit,
            c.EventType == "insulin" ? c.Product ?? profile.InsulinProduct : null,
            utc, local, zoneSnapshot, evidence, c.Unit is null ? "profile" : "stated",
            "text", source.Source.Id, c.Ordinal, source.Source.Id, null, null,
            source.Revision.Id, resultId, source.Source.SourceAuthorUserId, messageId,
            source.Source.TelegramMessageId), null);
    }

    private static bool TryTime(VetCandidate c, VetProfile p, DateTimeOffset sent,
        out DateTimeOffset utc, out string localText, out string snapshot, out string evidence, out string? reason)
    {
        utc = default; localText = ""; snapshot = ""; evidence = ""; reason = null;
        TimeZoneInfo? zone = null;
        if (p.TimeZone is { } tz)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(tz); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        if (c.Date is null && c.Time is null && c.TimeEvidence == "current")
        {
            if (zone is null) { reason = "Владелец должен один раз настроить часовой пояс через /settz."; return false; }
            utc = sent.ToUniversalTime();
            var display = TimeZoneInfo.ConvertTime(sent, zone);
            localText = display.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            snapshot = zone.Id; evidence = "message";
            return true;
        }
        if (c.Date is null || c.Time is null)
        {
            reason = "Уточните дату и время записи; историческое событие нельзя датировать временем сообщения.";
            return false;
        }
        DateOnly date;
        if (c.Date is "today" or "yesterday")
        {
            if (zone is null) { reason = "Для относительной даты нужен настроенный часовой пояс."; return false; }
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sent, zone).DateTime);
            if (c.Date == "yesterday") date = date.AddDays(-1);
            evidence = "relative";
        }
        else if (!DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            reason = "Уточните полную дату с годом."; return false;
        }
        else evidence = c.TimeEvidence == "context" ? "context" : "stated";
        if (!TimeOnly.TryParseExact(c.Time, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            reason = "Уточните время в формате HH:mm."; return false;
        }
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (c.Offset is { } offsetText)
        {
            if (offsetText.Length != 6 || offsetText[0] is not ('+' or '-') || offsetText[3] != ':'
                || !int.TryParse(offsetText.AsSpan(1, 2), out var hours)
                || !int.TryParse(offsetText.AsSpan(4, 2), out var minutes) || minutes > 59 || hours > 14
                || hours == 14 && minutes != 0)
            {
                reason = "Уточните смещение UTC."; return false;
            }
            var offset = new TimeSpan(hours, minutes, 0);
            if (offsetText[0] == '-') offset = -offset;
            try { utc = new DateTimeOffset(local, offset).ToUniversalTime(); }
            catch (ArgumentException) { reason = "Уточните дату/смещение."; return false; }
            snapshot = offsetText;
        }
        else
        {
            if (zone is null) { reason = "Укажите часовой пояс или явное смещение UTC."; return false; }
            if (zone.IsInvalidTime(local)) { reason = "Такого местного времени нет при переходе часов."; return false; }
            if (zone.IsAmbiguousTime(local)) { reason = "Это местное время повторяется: укажите смещение UTC."; return false; }
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            snapshot = zone.Id;
        }
        localText = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return true;
    }

    public static (DateTimeOffset From, DateTimeOffset Until)? DateRange(VetProfile p, string? first, string? last)
    {
        if (p.TimeZone is null || !DateOnly.TryParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var through)
            || through < from || through == DateOnly.MaxValue) return null;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZone);
            var a = from.ToDateTime(TimeOnly.MinValue);
            var b = through.AddDays(1).ToDateTime(TimeOnly.MinValue);
            if (zone.IsInvalidTime(a) || zone.IsInvalidTime(b) || zone.IsAmbiguousTime(a) || zone.IsAmbiguousTime(b)) return null;
            return (new(TimeZoneInfo.ConvertTimeToUtc(a, zone)), new(TimeZoneInfo.ConvertTimeToUtc(b, zone)));
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
