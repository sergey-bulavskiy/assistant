using System.Globalization;
using System.Text.Json;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

// Evidence text carries no authority. The caller stages all returned blocks with unchanged whole-preview bounds.
public static class VetPhotoReviewEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<string> Data(string label, string value)
    {
        const int payload = 3000;
        var chunks = new List<string>();
        for (var start = 0; start < value.Length;)
        {
            var end = Math.Min(start + payload, value.Length);
            if (end < value.Length && char.IsHighSurrogate(value[end - 1])) end--;
            chunks.Add(value[start..end]); start = end;
        }
        if (chunks.Count == 0) chunks.Add("");
        return chunks.Select((part, index) => label + " — часть " + (index + 1).ToString(CultureInfo.InvariantCulture)
            + "/" + chunks.Count.ToString(CultureInfo.InvariantCulture) + "\n" + part).ToArray();
    }
    public static VetEventState? State(VetEvent? e) => e == null ? null : new(e.EventType,e.Value,e.Unit,e.Product,
        e.OccurredAt,e.LocalTime,e.TimeZoneSnapshot,e.OccurredAtSource,e.ValueUnitSource,e.SourceKind,e.SourceId,e.CandidateOrdinal,
        e.TextSourceId,e.PhotoSourceId,e.PhotoBatchId,e.InputRevisionId,e.ExtractionResultId,e.SourceAuthorUserId,
        e.SourceMessageDbId,e.TelegramMessageId,e.DeletedAt,e.DeleteReason,e.DeletedByUserId);
    public static string StateText(string label, VetEventState? e) => e == null ? label + ": сохранённого факта нет." :
        $"{label}: {e.Value.ToString(CultureInfo.InvariantCulture)} {e.Unit}; тип {e.EventType}; продукт {e.Product ?? "нет"}; "
        + $"локальное время {e.LocalTime}; зона {e.TimeZoneSnapshot}; UTC {e.OccurredAt.ToUniversalTime():O}; "
        + $"основание времени {e.OccurredAtSource}; основание значения/единицы {e.ValueUnitSource}; вид источника {e.SourceKind}; "
        + $"источник {e.SourceId:D}; TEXT {e.TextSourceId}; фото {e.PhotoSourceId}; партия {e.PhotoBatchId}; вход {e.InputRevisionId:D}; результат {e.ExtractionResultId:D}; "
        + $"автор {e.SourceAuthorUserId}; исходное сообщение БД {e.SourceMessageDbId}; сообщение {e.TelegramMessageId}; кандидат {e.CandidateOrdinal}; "
        + $"удаление {e.DeletedAt?.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture) ?? "нет"}; причина {e.DeleteReason ?? "нет"}; удалил {e.DeletedByUserId}.";
    public static string EffectiveText(VetPhotoEffectiveReading? r) => r == null ? "Эффективное предложение: отсутствует; уточнение не заменяет дату загрузки." :
        $"Эффективное предложение: {r.Value.ToString(CultureInfo.InvariantCulture)} {r.Unit}; локальное время {r.LocalTime}; зона {r.TimeZoneSnapshot}; "
        + $"UTC {r.OccurredAt.ToUniversalTime():O}; значение={r.ValueEvidence}; единица={r.UnitEvidence}; время={r.TimeEvidence}; "
        + $"профильные допущения={(r.UsesProfileDefaults ? "да" : "нет")}.";
    public static string Assumptions(VetPhotoBatchAssumptions a) =>
        $"Допущения партии: год {a.Year?.ToString(CultureInfo.InvariantCulture) ?? "не задан"}; подтверждён {a.YearConfirmed}; "
        + $"единица {a.GlucoseUnit ?? "не задана"}; подтверждена {a.UnitConfirmed}; профильная единица {a.ProfileGlucoseUnit ?? "не задана"}; "
        + $"зона {a.TimeZone ?? "не задана"}; подтверждена {a.TimeZoneConfirmed}; профильная зона {a.ProfileTimeZone ?? "не задана"}.";
    public static IReadOnlyList<string> Candidate(VetPhotoCandidate c)
    {
        var result = new List<string> { $"Текущий кандидат {c.Id:D}, версия {c.Revision}: {c.State}; ручное исправление {c.ManuallyCorrected}; "
            + $"требует восстановления {c.RequiresExplicitRestoration}; решение о повторе {c.DuplicateDecision}; факт {c.EventId}, версия {c.EventRevision}; "
            + $"связанный факт {c.DuplicateEventId}, версия {c.DuplicateEventRevision}; связанный источник {c.DuplicateSourceId}; "
            + $"вход {c.InputRevisionId}; результат {c.ExtractionResultId}." };
        try
        {
            using var encoded = JsonDocument.Parse(c.EffectiveJson);
            if (encoded.RootElement.ValueKind == JsonValueKind.Object && encoded.RootElement.TryGetProperty("value", out _))
            {
                if (encoded.RootElement.TryGetProperty("valueEvidence", out _))
                    result.AddRange(Data("Прежнее эффективное состояние кандидата (данные)",
                        EffectiveText(JsonSerializer.Deserialize<VetPhotoEffectiveReading>(c.EffectiveJson, Json))));
                else result.AddRange(Data("Прежнее эффективное состояние кандидата (данные)",
                    StateText("Прежнее состояние", JsonSerializer.Deserialize<VetEventState>(c.EffectiveJson, Json))));
            }
            else result.Add("Прежнее эффективное состояние кандидата: отсутствует.");
        }
        catch (JsonException) { result.Add("Прежнее эффективное состояние кандидата: недоступно."); }
        VetPhotoContext? context;
        try { context = JsonSerializer.Deserialize<VetPhotoContext>(c.CorrectionProvenanceJson, Json); }
        catch (JsonException) { context = null; }
        result.AddRange(Data("Человеческое основание кандидата (данные)", context == null ? "нет" : Context(context)));
        return result;
    }
    public static string Context(VetPhotoContext c) =>
        $"значение {c.RawValue ?? "нет"}; единица {c.Unit ?? "нет"}; год {c.Year}; месяц {c.Month}; день {c.Day}; время {c.Time ?? "нет"}; "
        + $"смещение {c.Offset ?? "нет"}; человеческое подтверждение {c.CorrectionApproved}; неоднозначная граница года {c.YearBoundaryAmbiguous}; "
        + $"выбранный дисплей {c.SelectedDisplayIndex}; сохранённое время {c.PreservedTime?.LocalTime ?? "нет"}; "
        + $"сохранённая зона {c.PreservedTime?.TimeZoneSnapshot ?? "нет"}; сохранённое UTC {c.PreservedTime?.OccurredAt.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture) ?? "нет"}; "
        + $"сохранённое основание времени {c.PreservedTime?.TimeEvidence ?? "нет"}.";
    public static IReadOnlyList<string> Image(string label, VetPhotoPresentationEvidence e)
    {
        var parsed = e.Extraction == null ? null : VetPhotoInterpretationParser.Parse(e.Extraction.StructuredJson,e.Source.Id,e.Input.Id);
        var blocks = new List<string> { $"{label}: вход {e.Input.Id:D}; результат {e.Extraction?.Id.ToString("D") ?? "нет"}; модель {e.Extraction?.ModelName ?? "нет"}; "
            + $"исходный автор {e.Source.SourceAuthorUserId}; сохранённое исходное сообщение {e.Source.SourceMessageDbId}." };
        if (parsed == null) { blocks.Add(label + ": распознанный дисплей недоступен."); return blocks; }
        foreach (var d in parsed.Displays) blocks.Add($"{label}: видно {d.ValueText}; число {d.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "нет"}; "
            + $"единица {d.Unit ?? "нет"}; год {d.Year}, показан {d.YearDisplayed}; месяц {d.Month}; день {d.Day}; время {d.Time ?? "нет"}; смещение {d.Offset ?? "нет"}.");
        foreach (var reason in parsed.Reasons) blocks.AddRange(Data(label + ": неопределённость (данные)", reason));
        if (parsed.Notes != null) blocks.AddRange(Data(label + ": примечание изображения (данные)", parsed.Notes));
        return blocks;
    }
}
