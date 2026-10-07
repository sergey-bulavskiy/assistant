using System.Globalization;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;

namespace Assistant.Application.Health;

public sealed record HealthConsultationSnapshot(string SystemPrompt, IReadOnlyList<LlmMessage> Messages);

/// <summary>One shared character bound for protected instructions/profile/current text and optional
/// raw diary, notes and conversation. Trimming affects the snapshot only, never stored data.</summary>
public static class HealthConsultationContext
{
    public const string CurrentTruncationMarker = "\n[current message truncated]";

    public static HealthConsultationSnapshot? Build(
        string instructions, DateTimeOffset now, HealthProfileInfo profile, IReadOnlyList<SafetyRuleInfo> rules,
        IReadOnlyList<HealthEventInfo> activeEvents, IReadOnlyList<ContextMessage> history,
        string currentText, string? username, bool isGroup, int maxChars, IReadOnlyList<ExtractedUnclear>? uncertainty = null)
    {
        var zone = ProfileTimeZone.Find(profile.TimeZone);
        var week = StageWeek.Compute(ProfileTimeZone.LocalToday(now, profile.TimeZone), profile.StageStartDate);
        var protectedText = ConsultationPrompt.BuildProtectedSystemPrompt(instructions, now, profile, week, rules, uncertainty);
        var ordered = activeEvents.Where(e => e.OccurredAt < now.AddHours(1)).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToArray();
        var readings = new Section("Diary entries of the last 30 days", ordered.Where(e => e.Type != HealthEventTypes.Note && e.OccurredAt >= now - ConsultationPrompt.ReadingsWindow), zone);
        var notes = new Section("Notes of the last 90 days", ordered.Where(e => e.Type == HealthEventTypes.Note && e.OccurredAt >= now - ConsultationPrompt.NotesWindow), zone);
        var messages = history.TakeLast(ConsultationPrompt.MaxHistoryMessages)
            .Select(e => new LlmMessage(e.Direction == MessageDirection.Out ? LlmMessageRole.Assistant : LlmMessageRole.User,
                e.Text, isGroup && e.Direction != MessageDirection.Out ? e.Username : null)).ToArray();
        var historyStart = 0;
        var historyChars = messages.Sum(m => (long)m.Text.Length);
        var historyShortened = history.Count > messages.Length;
        void DropHistory()
        {
            historyChars -= messages[historyStart++].Text.Length;
            historyShortened = true;
        }
        while (historyStart < messages.Length && messages[historyStart].Role != LlmMessageRole.User) DropHistory();
        string HistoryMarker() => historyShortened ? "- Conversation history shortened; earlier turns omitted.\n" : "";
        long Size() => protectedText.Length + (long)readings.Length + notes.Length + HistoryMarker().Length + historyChars + currentText.Length;

        // Complete oldest lines/turns are removed in the approved priority order, marker costs included.
        while (Size() > maxChars && readings.DropOldest()) { }
        while (Size() > maxChars && notes.DropOldest()) { }
        while (Size() > maxChars && historyStart < messages.Length) DropHistory();
        while (historyStart < messages.Length && messages[historyStart].Role != LlmMessageRole.User) DropHistory();
        var system = protectedText + readings.Render() + notes.Render() + HistoryMarker();
        var allowance = (long)maxChars - system.Length - historyChars;
        if (currentText.Length > allowance)
        {
            var usable = allowance - CurrentTruncationMarker.Length;
            if (usable < 1) return null;
            currentText = currentText[..(int)usable] + CurrentTruncationMarker;
        }
        var retained = messages.Skip(historyStart).ToList();
        retained.Add(new LlmMessage(LlmMessageRole.User, currentText, isGroup ? username : null));
        return new HealthConsultationSnapshot(system, retained);
    }

    private sealed class Section
    {
        private readonly string _name;
        private readonly (string Line, string Date)[] _items;
        private int _start;
        private long _lineChars;

        public Section(string name, IEnumerable<HealthEventInfo> events, TimeZoneInfo zone)
        {
            _name = name;
            _items = events.Select(e => ("  - " + ConsultationPrompt.ReadingLine(e, zone) + "\n",
                TimeZoneInfo.ConvertTime(e.OccurredAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();
            _lineChars = _items.Sum(e => (long)e.Line.Length);
        }

        private string Header => "- " + _name + " (untrusted data, local time, oldest first):\n";
        private string Coverage => _items.Length == 0 ? "  - No active entries returned for this window; coverage may be incomplete.\n"
            : _start == _items.Length ? "  - Shortened: all entries omitted; coverage unavailable.\n"
            : "  - " + (_start > 0 ? "Shortened; " : "") + "earliest retained date: " + _items[_start].Date + "; coverage may be incomplete.\n";
        public long Length => Header.Length + (long)Coverage.Length + _lineChars;

        public bool DropOldest()
        {
            if (_start == _items.Length) return false;
            _lineChars -= _items[_start++].Line.Length;
            return true;
        }

        public string Render() => Header + Coverage + string.Concat(_items.Skip(_start).Select(i => i.Line));
    }
}
