using System.Globalization;

namespace Assistant.Application.Health.Documents;

public static class HealthDocumentList
{
    public static string Render(IReadOnlyList<HealthDocumentInfo> documents, string timeZone)
    {
        if (documents.Count == 0) return "Документов нет.";
        var zone = ProfileTimeZone.Find(timeZone);
        return "Документы (последние 10):\n" + string.Join("\n\n", documents.Take(10).Select(d =>
        {
            var date = TimeZoneInfo.ConvertTime(d.PostedAt, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            var status = d.AdmissionStatus == "paused" ? "ожидает восстановления разрешения"
                : d.TextStatus == "read" ? d.TextTruncated ? "прочитан частично" : "текст прочитан"
                : d.TextStatus == "processing" ? d.NextAttemptAt is { } retry
                    ? "повторная загрузка после " + TimeZoneInfo.ConvertTime(retry, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                    : "обрабатывается"
                : "текст не прочитан: " + Reason(d.FailureReason);
            var caption = string.IsNullOrEmpty(d.Caption) ? "" : "\nПодпись: " + HealthDocumentContextSection.SafePrefix(d.Caption, 500)
                + (d.Caption.Length > 500 ? " [подпись сокращена]" : "");
            return date + " — " + HealthDocumentContextSection.DisplayName(d.FileName) + " — " + status + caption;
        }));
    }

    private static string Reason(string? reason) => reason switch
    {
        "unsupported_format" => "формат не поддерживается",
        "too_large" => "файл больше 20 МБ",
        "no_readable_text" => "нет читаемого текста; фото и сканы будут позже",
        "invalid_encoding" => "некорректный UTF-8",
        "binary_content" => "не текстовый файл",
        "invalid_pdf" => "PDF повреждён или некорректен",
        "encrypted_pdf" => "PDF защищён паролем",
        "unavailable" => "файл недоступен; отправьте ещё раз",
        "timeout" => "время загрузки истекло; отправьте ещё раз",
        "interrupted" => "попытки обработки исчерпаны; отправьте ещё раз",
        _ => "прочитать файл не удалось"
    };
}
