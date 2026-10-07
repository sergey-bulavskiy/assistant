using Assistant.Application.Vet;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Vet;

public sealed class VetHistoryTests : VetTestBase
{
    [Fact]
    public async Task History_pages_fifty_mixed_old_records_and_more_returns_the_remaining_record_without_smart_call()
    {
        await SeedAsync(); await using var s = Open();
        await PopulateAsync(s, 51);
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[],"history_query":{"from_date":"2010-01-01","until_date":"2010-01-31","offset":0,"analysis":false}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(100, Text("synthetic older month request", 2000)), CancellationToken.None);
        var firstPage = string.Join("\n", s.Telegram.SentMessages.Select(m => m.Text));
        firstPage.ShouldContain("Показано 50, пропущено ранее 0, всего 51");
        firstPage.ShouldContain("глюкоза 1 mmol/L 2010-01-02 00:00:00");
        firstPage.ShouldContain("инсулин 50 U"); firstPage.ShouldNotContain("глюкоза 51 mmol/L");
        var boundary = s.Telegram.SentMessages.Count;
        await s.Handler.HandleAsync(Bot, s.Telegram, new(101, Text("/more", 2001)), CancellationToken.None);
        var continuation = string.Join("\n", s.Telegram.SentMessages.Skip(boundary).Select(m => m.Text));
        continuation.ShouldContain("Показано 1, пропущено ранее 50, всего 51");
        continuation.ShouldContain("глюкоза 51 mmol/L 2010-01-02 00:50:00");
        continuation.ShouldNotContain("инсулин 50 U"); continuation.ShouldContain("Период просмотрен полностью");
        s.Chat.RequestedMessages.Count.ShouldBe(1);
        (await s.Context.LlmCalls.Select(c => c.Tier).ToListAsync()).ShouldBe(new[] { "fast" });
    }

    [Fact]
    public async Task Analysis_uses_latest_two_hundred_with_truthful_omission_and_default_recent_window_excludes_old_data()
    {
        await SeedAsync(); await using var s = Open();
        await PopulateAsync(s, 205);
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[],"history_query":{"from_date":"2010-01-01","until_date":"2010-01-31","offset":0,"analysis":true}}""");
        s.Chat.EnqueueResponse("synthetic bounded older-history answer");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1000, Text("synthetic old history analysis", 2000)), CancellationToken.None);
        var prompt = s.Chat.RequestedMessages[1][0].Text!;
        prompt.ShouldContain("Supplied 200 of 205; omitted 5");
        prompt.ShouldContain("Older records omitted; do not claim inspection");
        prompt.ShouldNotContain("глюкоза 1 mmol/L"); prompt.ShouldContain("инсулин 6 U");
        prompt.ShouldContain("глюкоза 205 mmol/L");
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[]}""");
        s.Chat.EnqueueResponse("synthetic current-window answer");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1001, Text("synthetic current question", 2001)), CancellationToken.None);
        var current = s.Chat.RequestedMessages[3][0].Text!;
        current.ShouldContain("2031-05-06T00:00:00"); current.ShouldContain("2031-05-13T00:00:00");
        current.ShouldContain("Supplied 0 of 0; omitted 0"); current.ShouldNotContain("глюкоза 205 mmol/L");
        (await s.Context.LlmCalls.OrderBy(c => c.Id).Select(c => c.Tier).ToListAsync())
            .ShouldBe(new[] { "fast", "smart", "fast", "smart" });
    }

    [Fact]
    public async Task Ambiguous_date_type_correction_does_not_select_an_arbitrary_record()
    {
        await SeedAsync(); await using var s = Open();
        await PopulateAsync(s, 3);
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"9.1","date":"2010-01-02"}],"operation":{"kind":"correct"}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(100, Text("synthetic ambiguous day correction", 2000)), CancellationToken.None);
        (await s.Context.VetEvents.OrderBy(e => e.Id).Select(e => e.Value).ToListAsync()).ShouldBe(new[] { 1m, 2m, 3m });
        (await s.Context.VetEvents.Select(e => e.Revision).ToListAsync()).ShouldBe(new[] { 1, 1, 1 });
        s.Telegram.ButtonMessages.Single().Buttons.Select(b => b.Label).ShouldBe(new[] { "Отменить" });
    }

    private async Task PopulateAsync(VetTestSession session, int count)
    {
        for (var offset = 0; offset < count; offset += 20)
        {
            var evidence = await EvidenceAsync(session, id: 1000 + offset, update: 1 + offset);
            var states = Enumerable.Range(offset, Math.Min(20, count - offset)).Select(index =>
            {
                var occurred = DateTimeOffset.Parse("2010-01-02T00:00:00Z").AddMinutes(index);
                return evidence.State with { EventType = index % 2 == 0 ? "glucose" : "insulin",
                    Value = index + 1m, Unit = index % 2 == 0 ? "mmol/L" : "U", CandidateOrdinal = index - offset,
                    OccurredAt = occurred, LocalTime = occurred.ToString("yyyy-MM-dd HH:mm:ss"), OccurredAtSource = "stated" };
            }).ToArray();
            (await session.Diary.ApplyAsync(Save(Scope, evidence.Source, evidence.Profile, states), CancellationToken.None))
                .Status.ShouldBe(VetMutationStatus.Applied);
        }
    }
}
