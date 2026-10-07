using System.Globalization;
using Assistant.Domain.Vet;

namespace Assistant.Application.Vet;

public static class VetEventText
{
    public static VetEventState State(VetEvent e) => new(e.EventType, e.Value, e.Unit, e.Product,
        e.OccurredAt, e.LocalTime, e.TimeZoneSnapshot, e.OccurredAtSource, e.ValueUnitSource,
        e.SourceKind, e.SourceId, e.CandidateOrdinal, e.TextSourceId, e.PhotoSourceId, e.PhotoBatchId,
        e.InputRevisionId, e.ExtractionResultId, e.SourceAuthorUserId, e.SourceMessageDbId,
        e.TelegramMessageId, e.DeletedAt, e.DeleteReason, e.DeletedByUserId);

    public static string Describe(VetEventState s) =>
        $"{(s.EventType == "glucose" ? "глюкоза" : "инсулин")} {Number(s.Value)} {s.Unit}"
        + (s.EventType == "insulin" ? $" ({s.Product ?? "препарат не указан"})" : "")
        + $" — {s.LocalTime} [{s.TimeZoneSnapshot}]"
        + (s.OccurredAtSource == "message" ? " (время исходного сообщения)" : "")
        + (s.ValueUnitSource == "profile" ? " (единица из профиля)" : "");

    public static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    public static string Profile(VetProfile p) =>
        $"Профиль: {p.Name ?? "кот этого бота"}; версия {p.Revision}.\n"
        + $"Часовой пояс: {p.TimeZone ?? "не настроен (/settz)"}\n"
        + $"Глюкоза: {p.GlucoseUnit ?? "единица не настроена (/setunit)"}\n"
        + $"Инсулин: {p.InsulinUnit ?? "единица не настроена (/setinsulin)"}, {p.InsulinProduct ?? "препарат не указан"}\n"
        + $"Контекст владельца: {p.OwnerContextNote ?? "не указан"}\n"
        + $"Со слов владельца — рекомендации ветеринара: {p.ReportedVetGuidance ?? "не указаны"}";
}

public sealed record VetProposal(IReadOnlyList<VetCandidate> Candidates,
    IReadOnlyList<VetEventChange> Changes, IReadOnlyList<string> Reasons, string Kind, bool RequiresTargetSelection = false);

public sealed record VetTextPlan(IReadOnlyList<VetEventChange> ClearChanges, VetProposal? Pending);

public static class VetTextPlanner
{
    public static VetTextPlan Plan(IReadOnlyList<VetValidation> validated, IReadOnlyList<VetEvent> old,
        bool isEdit, long actor, DateTimeOffset now)
    {
        var clear = validated.Where(v => v.State is not null).ToList();
        var unresolved = validated.Where(v => v.Reason is not null).ToList();
        if (!isEdit)
            return new(clear.Select(v => new VetEventChange(null, null, v.State!)).ToArray(),
                unresolved.Count == 0 ? null : new(unresolved.Select(v => v.Candidate).ToArray(), [],
                    unresolved.Select(v => v.Reason!).ToArray(), "confirm"));
        var changes = new List<VetEventChange>();
        var reasons = unresolved.Select(v => v.Reason!).ToList();
        var remaining = old.ToList();
        var unmatched = new List<VetValidation>();
        var requiresTargets = false;
        foreach (var v in clear)
        {
            var matches = remaining.Where(e => Same(VetEventText.State(e), v.State!)).ToList();
            if (matches.Count == 1) { remaining.Remove(matches[0]); continue; }
            if (matches.Count > 1) { reasons.Add("Несколько одинаковых фактов: укажите ID для точного сопоставления."); requiresTargets = true; }
            unmatched.Add(v);
        }
        foreach (var type in new[] { "glucose", "insulin" })
        {
            var proposed = unmatched.Where(v => v.Candidate.EventType == type).ToList();
            var existing = remaining.Where(e => e.EventType == type).ToList();
            if (proposed.Count > 1 && existing.Count > 0 || proposed.Count > 0 && existing.Count > 1)
            {
                reasons.Add("Изменение нескольких фактов требует точных ID; прежние записи сохранены.");
                requiresTargets = true;
                continue;
            }
            if (proposed.Count == 1 && existing.Count == 1)
            {
                var e = existing[0];
                if (e.LastMutationKind is not ("save" or "edit" or "confirm") || e.DeletedAt is not null)
                    reasons.Add($"Запись #{e.Id} меняли отдельно; подтвердите точное исправление.");
                var state = proposed[0].State! with { CandidateOrdinal = e.CandidateOrdinal };
                changes.Add(new(e.Id, e.Revision, state));
                remaining.Remove(e);
            }
            else if (proposed.Count > 0 && existing.Count == 0)
            {
                var next = old.Where(e => e.EventType == type).Select(e => e.CandidateOrdinal).DefaultIfEmpty(-1).Max() + 1;
                foreach (var v in proposed) changes.Add(new(null, null, v.State! with { CandidateOrdinal = next++ }));
            }
        }
        // Uncertain interpretation never deletes a previously confirmed source fact.
        if (unresolved.Count == 0)
        {
            foreach (var e in remaining.Where(e => e.DeletedAt is null))
            {
                if (e.LastMutationKind is not ("save" or "edit" or "confirm"))
                {
                    reasons.Add($"Запись #{e.Id} меняли отдельно; прежнее состояние защищено.");
                    continue;
                }
                changes.Add(new(e.Id, e.Revision, VetEventText.State(e) with
                    { DeletedAt = now, DeleteReason = "source_edit", DeletedByUserId = actor }));
            }
        }
        if (reasons.Count > 0)
            return new([], new(validated.Where(v => v.Candidate.Intent != "question_only").Select(v => v.Candidate).ToArray(),
                changes, reasons, "edit", requiresTargets));
        return new(changes, null);
    }

    private static bool Same(VetEventState a, VetEventState b) => a.EventType == b.EventType
        && a.Value == b.Value && a.Unit == b.Unit && a.Product == b.Product
        && a.OccurredAt == b.OccurredAt && a.DeletedAt is null;
}
