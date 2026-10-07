using Assistant.Application.Health.Documents;

namespace Assistant.UnitTests.Application.Health;

public sealed class HealthDocumentListTests
{
    [Fact]
    public void List_uses_local_year_status_missing_name_and_bounded_caption()
    {
        var at = new DateTimeOffset(2030, 12, 31, 20, 0, 0, TimeSpan.Zero);
        var documents = new[]
        {
            new HealthDocumentInfo(1, 11, at, null, new string('c', 501), "read", null, true, "completed", null),
            new HealthDocumentInfo(2, 12, at, "synthetic.txt", null, "processing", "unavailable", false, "admitted", at.AddMinutes(1)),
            new HealthDocumentInfo(3, 13, at, "synthetic.pdf", null, "processing", null, false, "paused", null)
        };
        var text = HealthDocumentList.Render(documents, "Asia/Tokyo");
        text.ShouldContain("01.01.2031 05:00 — документ — прочитан частично");
        text.ShouldContain(new string('c', 500) + " [подпись сокращена]");
        text.ShouldNotContain(new string('c', 501));
        text.ShouldContain("повторная загрузка после 01.01.2031 05:01");
        text.ShouldContain("ожидает восстановления разрешения");
    }

    [Fact]
    public void Empty_library_has_a_concise_reply()
    {
        HealthDocumentList.Render([], "UTC").ShouldBe("Документов нет.");
    }
}
