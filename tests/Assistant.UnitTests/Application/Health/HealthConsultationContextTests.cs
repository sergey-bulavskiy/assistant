using Assistant.Application.Health;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Application.Health;

public class HealthConsultationContextTests
{
    private static readonly DateTimeOffset Now = new(2030, 4, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly HealthProfileInfo Profile = new(1, new DateOnly(2030, 4, 1), "UTC", "excluded-emergency", "context-marker",
        "condition-marker", "medication-marker", "allergy-marker", "plan-marker", "contact-marker");

    private static HealthEventInfo Reading(long id, DateTimeOffset at) => new(id, "weight", at, "{\"kg\":64.5}", id);
    private static HealthEventInfo Note(long id, DateTimeOffset at, string text) => new(id, "note", at,
        HealthEventPayloads.Serialize(new NotePayload(text, new[] { "walk" })), id);
    private static HealthConsultationSnapshot? Build(IReadOnlyList<HealthEventInfo>? events = null,
        IReadOnlyList<ContextMessage>? history = null, string current = "current-marker", int budget = 100000,
        HealthProfileInfo? profile = null) =>
        HealthConsultationContext.Build("protected-instructions", Now, profile ?? Profile, [], events ?? [], history ?? [], current,
            "synthetic-author", true, budget);
    private static long Size(HealthConsultationSnapshot snapshot) => snapshot.SystemPrompt.Length + snapshot.Messages.Sum(m => (long)m.Text.Length);

    [Fact]
    public void Independent_windows_include_old_notes_once_and_exclude_old_readings_and_future_boundary()
    {
        var events = new[]
        {
            Reading(1, Now.AddDays(-29)), Reading(2, Now.AddDays(-31)),
            Note(3, Now.AddDays(-31), "note31"), Note(4, Now.AddDays(-89), "note89"),
            Note(5, Now.AddDays(-91), "excluded91"), Note(6, Now.AddDays(-1), "recent-once"),
            Reading(7, Now.AddHours(1)), Reading(8, Now.AddHours(1).AddTicks(-1))
        };
        var snapshot = Build(events)!;
        snapshot.SystemPrompt.ShouldContain("2030-03-12 10:00 вес 64.5");
        snapshot.SystemPrompt.ShouldNotContain("2030-03-10 10:00 вес 64.5");
        snapshot.SystemPrompt.ShouldContain("note31");
        snapshot.SystemPrompt.ShouldContain("note89");
        snapshot.SystemPrompt.ShouldNotContain("excluded91");
        snapshot.SystemPrompt.Split("recent-once").Length.ShouldBe(2);
        snapshot.SystemPrompt.ShouldNotContain("2030-04-10 11:00 вес");
        snapshot.SystemPrompt.ShouldContain("2030-04-10 10:59 вес");
        snapshot.Messages.ShouldHaveSingleItem().Text.ShouldBe("current-marker");
    }

    [Fact]
    public void Window_lower_bounds_are_inclusive_and_order_uses_event_id_for_ties()
    {
        var at = Now.AddDays(-30);
        var snapshot = Build([Reading(1, at), Reading(2, at.AddTicks(-1)), Note(4, Now.AddDays(-90), "second"),
            Note(3, Now.AddDays(-90), "first"), Note(5, Now.AddDays(-90).AddTicks(-1), "excluded")])!;
        snapshot.SystemPrompt.ShouldContain("2030-03-11 10:00 вес");
        snapshot.SystemPrompt.ShouldNotContain("2030-03-11 09:59 вес");
        snapshot.SystemPrompt.ShouldNotContain("excluded");
        snapshot.SystemPrompt.IndexOf("заметка: first", StringComparison.Ordinal)
            .ShouldBeLessThan(snapshot.SystemPrompt.IndexOf("заметка: second", StringComparison.Ordinal));
    }

    [Fact]
    public void Large_windows_have_no_fifty_reading_or_twenty_note_count_ceiling()
    {
        var events = Enumerable.Range(1, 70).Select(i => Reading(i, Now.AddMinutes(-i)))
            .Concat(Enumerable.Range(71, 30).Select(i => Note(i, Now.AddDays(-40).AddMinutes(i), $"note-{i}"))).ToArray();
        var text = Build(events)!.SystemPrompt;
        text.Split("вес 64.5").Length.ShouldBe(71);
        text.Split("заметка: note-").Length.ShouldBe(31);
        text.ShouldNotContain("Shortened");
    }

    [Fact]
    public void Profile_and_local_runtime_are_protected_and_emergency_number_is_excluded()
    {
        var snapshot = Build(profile: Profile with { TimeZone = "Asia/Tokyo" })!;
        foreach (var marker in new[] { "condition-marker", "medication-marker", "allergy-marker", "plan-marker", "contact-marker", "context-marker" })
            snapshot.SystemPrompt.ShouldContain(marker);
        snapshot.SystemPrompt.ShouldContain("2030-04-10 19:00");
        snapshot.SystemPrompt.ShouldContain("1 нед. 2 дн.");
        snapshot.SystemPrompt.ShouldNotContain("excluded-emergency");
        snapshot.Messages[^1].Author.ShouldBe("synthetic-author");
    }

    [Fact]
    public void Oldest_reading_lines_are_removed_before_notes_and_history_with_accurate_coverage()
    {
        var events = new[] { Reading(1, Now.AddDays(-10)), Reading(2, Now.AddDays(-1)), Note(3, Now.AddDays(-40), "protected-optional-note") };
        var history = new[] { new ContextMessage(MessageDirection.In, "member", "previous-turn", Now.AddMinutes(-1)) };
        var full = Build(events, history)!;
        var budget = (int)Size(full) - 1;
        var trimmed = Build(events, history, budget: budget)!;
        Size(trimmed).ShouldBeLessThanOrEqualTo(budget);
        trimmed.SystemPrompt.ShouldNotContain("2030-03-31 10:00 вес");
        trimmed.SystemPrompt.ShouldContain("2030-04-09 10:00 вес");
        trimmed.SystemPrompt.ShouldContain("Shortened; earliest retained date: 2030-04-09");
        trimmed.SystemPrompt.ShouldContain("protected-optional-note");
        trimmed.Messages.Select(m => m.Text).ShouldBe(new[] { "previous-turn", "current-marker" });
        events[0].OccurredAt.ShouldBe(Now.AddDays(-10));
    }

    [Fact]
    public void Local_day_boundary_changes_stage_week_and_diary_dates_together()
    {
        var late = new DateTimeOffset(2030, 4, 10, 22, 30, 0, TimeSpan.Zero);
        var snapshot = HealthConsultationContext.Build("instructions", late, Profile with { TimeZone = "Asia/Tokyo" }, [],
            [Reading(1, late.AddMinutes(-1))], [], "current", null, false, 100000)!;
        snapshot.SystemPrompt.ShouldContain("Current local date and time: 2030-04-11 07:30");
        snapshot.SystemPrompt.ShouldContain("Stage week: 1 нед. 3 дн.");
        snapshot.SystemPrompt.ShouldContain("2030-04-11 07:29 вес 64.5");
        snapshot.Messages[^1].Author.ShouldBeNull();
    }

    [Fact]
    public void Shared_bound_removes_readings_then_notes_then_history_before_truncating_current()
    {
        var events = new[] { Reading(1, Now.AddDays(-1)), Note(2, Now.AddDays(-40), new string('n', 500)) };
        var history = new[] { new ContextMessage(MessageDirection.In, "member", new string('h', 500), Now.AddMinutes(-1)) };
        var baseline = Build()!;
        var budget = (int)Size(baseline) + 30;
        var snapshot = Build(events, history, budget: budget)!;
        Size(snapshot).ShouldBeLessThanOrEqualTo(budget);
        snapshot.SystemPrompt.Split("Shortened: all entries omitted").Length.ShouldBe(3);
        snapshot.SystemPrompt.ShouldContain("Conversation history shortened");
        snapshot.Messages.ShouldHaveSingleItem().Text.ShouldBe("current-marker");
        snapshot.SystemPrompt.ShouldContain("condition-marker");
    }

    [Fact]
    public void Current_turn_truncation_is_explicit_and_tiny_budget_fails_locally()
    {
        var baseline = Build(current: "c")!;
        var budget = baseline.SystemPrompt.Length + HealthConsultationContext.CurrentTruncationMarker.Length + 5;
        var truncated = Build(current: new string('c', 1000), budget: budget)!;
        Size(truncated).ShouldBe(budget);
        truncated.Messages.ShouldHaveSingleItem().Text.ShouldBe("ccccc" + HealthConsultationContext.CurrentTruncationMarker);
        Build(current: "c", budget: baseline.SystemPrompt.Length).ShouldBeNull();
        Profile.Conditions.ShouldBe("condition-marker");
    }

    [Fact]
    public void Retained_conversation_starts_with_user_and_ends_with_current_once()
    {
        var history = new[]
        {
            new ContextMessage(MessageDirection.Out, null, "orphan-answer", Now.AddMinutes(-3)),
            new ContextMessage(MessageDirection.In, "member", "earlier-user", Now.AddMinutes(-2)),
            new ContextMessage(MessageDirection.Out, null, "earlier-answer", Now.AddMinutes(-1))
        };
        var snapshot = Build(history: history)!;
        snapshot.Messages.Select(m => m.Text).ShouldBe(new[] { "earlier-user", "earlier-answer", "current-marker" });
        snapshot.Messages[0].Role.ShouldBe(LlmMessageRole.User);
        snapshot.Messages[^1].Role.ShouldBe(LlmMessageRole.User);
        snapshot.SystemPrompt.ShouldContain("Conversation history shortened");
    }

    [Fact]
    public void Recent_text_history_has_at_most_ten_entries_without_changing_current_turn()
    {
        var history = Enumerable.Range(0, 12).Select(i => new ContextMessage(MessageDirection.In, "member", $"history-{i}", Now.AddMinutes(i - 12))).ToArray();
        var snapshot = Build(history: history)!;
        snapshot.Messages.Count.ShouldBe(11);
        snapshot.Messages[0].Text.ShouldBe("history-2");
        snapshot.Messages[^1].Text.ShouldBe("current-marker");
    }
}
