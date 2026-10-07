using System.Globalization;
using System.Text.Json;
using Assistant.Application.Diagnostics;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet;

public sealed partial class VetAssistant
{
    public async Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct)
    {
        if (bot.FamilyId is null || callback.MessageId <= 0 || callback.MessageChatId == 0
            || callback.MessageChatType is not ("private" or "group" or "supergroup"))
        { await _replies.CallbackAsync(client, callback.CallbackQueryId, null, ct); return; }
        var message = new IncomingMessage(callback.MessageChatId, callback.MessageChatType, null,
            callback.MessageTopicId, callback.MessageId, callback.FromUserId, null, null,
            Assistant.Domain.Messages.MessageKind.Text, false, _clock.UtcNow, null, null, "{}",
            null, null);
        if (!await AuthorizedAsync(bot, message, ct))
        { await _replies.CallbackAsync(client, callback.CallbackQueryId, UpdateHandler.NoRightsText, ct); return; }
        var parts = callback.Data.Split(':');
        if (parts.Length != 4 || parts[0] != "v" || parts[1] is not ("a" or "d")
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
        { await _replies.CallbackAsync(client, callback.CallbackQueryId, null, ct); return; }
        var scope = VetDiaryScope.From(bot, message);
        var decision = await _diary.GetPendingAsync(scope, id, ct);
        if (decision is null || decision.PromptMessageId != callback.MessageId || decision.ReviewRevision != revision
            || decision.State != "pending" || decision.ExpiresAt <= _clock.UtcNow)
        { await _replies.CallbackAsync(client, callback.CallbackQueryId, "Этот просмотр уже недействителен.", ct); return; }
        var profile = await _profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, ct);
        var notice = parts[1] == "d"
            ? await _diary.DeclineAsync(scope, id, revision, callback.FromUserId, ct) ? "Предложение отменено; подтверждённый дневник не изменён." : "Этот просмотр уже недействителен."
            : await AcceptAsync(bot, client, message, profile, decision, ct);
        await _replies.CallbackAsync(client, callback.CallbackQueryId, notice, ct);
        try { await client.EditMessageButtonsAsync(callback.MessageChatId, callback.MessageId, [], ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { _logger.LogWarning("Vet review buttons failed: {ExceptionType}", ex.GetType().Name); }
    }

    private async Task ResolveNaturalAsync(VetOperation op, IReadOnlyList<VetCandidate> supplied, ReceivingBot bot,
        ITelegramClient client, IncomingMessage message, VetAdmittedSource clarification, VetProfile profile, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        var pending = await _diary.GetPendingAsync(scope, ct);
        VetPendingDecision? decision = null;
        if (op.PendingId is { } id)
        {
            // A model-supplied target must also be tied to the exact shown review revision.
            decision = pending.SingleOrDefault(p => p.Id == id && p.ReviewRevision == op.ReviewRevision);
        }
        else if (message.ReplyToMessageId is { } reply && message.TopicId != reply)
        {
            var source = await _diary.FindSourceAsync(scope, reply, ct);
            var candidates = pending.Where(p => p.PromptMessageId == reply || source is not null && p.SourceId == source.Source.Id).ToArray();
            if (candidates.Length == 1) decision = candidates[0];
        }
        else if (pending.Count == 1 && pending[0].PromptMessageId is not null)
            decision = pending[0];
        if (decision is null)
        { await _replies.SendAsync(client, message, "Ответьте на конкретный текущий просмотр; несколько предложений нельзя подтвердить одним неопределённым согласием.", ct); return; }
        if (op.Kind == "decline")
        {
            var declined = await _diary.DeclineAsync(scope, decision.Id, decision.ReviewRevision, message.UserId!.Value, ct);
            await _replies.SendAsync(client, message, declined ? "Предложение отменено; дневник не изменён." : "Этот просмотр уже недействителен.", ct);
            return;
        }
        if (supplied.Count > 0)
        {
            var proposal = JsonSerializer.Deserialize<VetProposal>(decision.ProposalJson)!;
            if (proposal.RequiresTargetSelection)
            {
                await _replies.SendAsync(client, message, "Укажите отдельные исправления по ID; неопределённое сопоставление не подтверждается целиком.", ct); return;
            }
            bool Matches(VetCandidate original, VetCandidate replacement)
            {
                if (original.EventType != replacement.EventType) return false;
                if (replacement.EventId is { } eventId) return original.EventId == eventId;
                var sameType = proposal.Candidates.Where(c => c.EventType == replacement.EventType).ToArray();
                if (sameType.Length == 1) return true;
                return VetInterpretationParser.TryPositiveDecimal(replacement.RawValue, out var value)
                    && VetInterpretationParser.TryPositiveDecimal(original.RawValue, out var originalValue) && value == originalValue
                    && sameType.Count(c => VetInterpretationParser.TryPositiveDecimal(c.RawValue, out var candidateValue) && candidateValue == value) == 1;
            }
            if (supplied.Any(replacement => proposal.Candidates.Count(original => Matches(original, replacement)) != 1))
            {
                await _replies.SendAsync(client, message,
                    "Уточните, какой факт исправляете: укажите его исходное значение или точный ID. Неоднозначное уточнение не меняет просмотр.", ct);
                return;
            }
            var merged = new List<VetCandidate>();
            foreach (var original in proposal.Candidates)
            {
                var replacements = supplied.Where(c => Matches(original, c)).ToArray();
                if (replacements.Length > 1) { await _replies.SendAsync(client, message, "Уточните, какой факт исправляете.", ct); return; }
                var replacement = replacements.SingleOrDefault();
                merged.Add(replacement is null ? original : original with
                {
                    RawValue = replacement.RawValue ?? original.RawValue, Unit = replacement.Unit ?? original.Unit,
                    Product = replacement.Product ?? original.Product, Date = replacement.Date ?? original.Date,
                    Time = replacement.Time ?? original.Time, Offset = replacement.Offset ?? original.Offset,
                    TimeEvidence = replacement.Date is not null || replacement.Time is not null ? replacement.TimeEvidence : original.TimeEvidence,
                    Intent = "record"
                });
            }
            var clarificationResult = await _diary.GetResultAsync(scope, clarification.Revision.Id, ct);
            var revised = proposal with { Candidates = merged, Changes = [],
                ClarificationInputRevisionId = clarification.Revision.Id, ClarificationResultId = clarificationResult!.Id,
                RequiresClarification = false, Reasons = [] };
            if (proposal.Kind == "manual")
            {
                var changes = new List<VetEventChange>();
                var reasons = new List<string>();
                foreach (var candidate in merged)
                {
                    var target = candidate.EventId is { } targetId
                        ? await _diary.GetEventAsync(scope, targetId, ct) : null;
                    if (target is null) { reasons.Add("Укажите точный ID исправляемой записи."); continue; }
                    var validation = Correction(candidate, target, profile, clarification, clarificationResult.Id);
                    if (validation.State is not { } state || state.EventType != target.EventType)
                    { reasons.Add(validation.Reason ?? "Уточните исправление."); continue; }
                    changes.Add(new(target.Id, target.Revision, state with
                    {
                        SourceKind = target.SourceKind, SourceId = target.SourceId, TextSourceId = target.TextSourceId,
                        CandidateOrdinal = target.CandidateOrdinal, SourceAuthorUserId = target.SourceAuthorUserId,
                        SourceMessageDbId = target.SourceMessageDbId, TelegramMessageId = target.TelegramMessageId
                    }));
                }
                revised = revised with { Changes = changes, Reasons = reasons, RequiresClarification = reasons.Count > 0 };
            }
            else if (proposal.Kind == "edit")
            {
                var originalSource = (await _diary.GetSourceAsync(scope, decision.SourceId, ct))!;
                var validations = merged.Select(c => VetEventValidation.Validate(c with { Intent = "record" },
                    profile, originalSource, decision.ExtractionResultId)).ToArray();
                var planned = VetTextPlanner.Plan(validations,
                    await _diary.GetSourceEventsAsync(scope, decision.SourceId, ct), true, message.UserId!.Value, _clock.UtcNow);
                var changes = planned.Pending?.Changes ?? planned.ClearChanges;
                revised = revised with
                {
                    Changes = changes.Select(c => c with { State = c.State with
                        { InputRevisionId = clarification.Revision.Id, ExtractionResultId = clarificationResult.Id } }).ToArray(),
                    Reasons = planned.Pending?.Reasons ?? [], RequiresTargetSelection = planned.Pending?.RequiresTargetSelection ?? false,
                    RequiresClarification = planned.Pending?.RequiresClarification ?? false
                };
            }
            if (!await _diary.RevisePendingAsync(scope, decision.Id, decision.ReviewRevision, JsonSerializer.Serialize(revised), ct))
            { await _replies.SendAsync(client, message, "Просмотр изменился параллельно; ничего не сохранено.", ct); return; }
            decision = (await _diary.GetPendingAsync(scope, decision.Id, ct))!;
            await ShowPendingAsync(client, message, decision, profile, ct);
            return;
        }
        var notice = await AcceptAsync(bot, client, message, profile, decision, ct);
        await _replies.SendAsync(client, message, notice, ct);
    }

    private async Task<string> AcceptAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetProfile profile, VetPendingDecision decision, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        if (!await AuthorizedAsync(bot, message, ct)) return UpdateHandler.NoRightsText;
        var source = await _diary.GetSourceAsync(scope, decision.SourceId, ct);
        if (source is null || source.Revision.Id != decision.InputRevisionId || decision.State != "pending"
            || decision.ExpiresAt <= _clock.UtcNow) return "Этот просмотр уже недействителен.";
        if (decision.PromptMessageId is null)
        {
            await ShowPendingAsync(client, message, decision, profile, ct);
            return "Сначала проверьте полный отправленный просмотр и подтвердите его отдельно. Ничего ещё не сохранено.";
        }
        var proposal = JsonSerializer.Deserialize<VetProposal>(decision.ProposalJson)!;
        if (proposal.RequiresTargetSelection) return "Нужны точные ID исправляемых фактов; прежние записи сохранены.";
        if (proposal.RequiresClarification) return "Сначала уточните недостающие данные; прежние записи сохранены.";
        var changes = proposal.Changes.ToList();
        if (proposal.Kind == "confirm" && proposal.Changes.Count == 0)
        {
            await ShowPendingAsync(client, message, decision, profile, ct);
            return "Проверьте новый просмотр с уточнёнными данными и подтвердите его. Ничего ещё не сохранено.";
        }
        if (changes.Count == 0) return "Нужны точные значение, единица и время/ID. Ничего не сохранено.";
        // The reviewed state is frozen in ProposalJson. A later profile change never substitutes
        // newly inferred defaults into an unseen review.
        var result = await _diary.ApplyAsync(new(scope, decision.OperationKey, message.UserId!.Value,
            proposal.Kind == "confirm" ? "confirm" : proposal.Kind, profile.Id, changes,
            decision.SourceId, decision.InputRevisionId, decision.Id, decision.ReviewRevision), ct);
        await TraceSafety.RecordAsync(_trace, new("confirmation",
            result.Status is VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied ? "accepted" : "skipped", "normal",
            PendingRecordId: decision.Id, RelatedSourceMessageId: source.Source.SourceMessageDbId,
            ActorId: message.UserId));
        return MutationText(changes, result);
    }

    private async Task ShowPendingAsync(ITelegramClient client, IncomingMessage message, VetPendingDecision decision,
        VetProfile profile, CancellationToken ct)
    {
        if (decision.State != "pending") return;
        var scope = new VetDiaryScope(decision.FamilyId, decision.BotDbId, decision.TelegramBotId, decision.ChatId, decision.TopicId);
        var source = await _diary.GetSourceAsync(scope, decision.SourceId, ct);
        if (source is null || source.Revision.Id != decision.InputRevisionId) return;
        var proposal = JsonSerializer.Deserialize<VetProposal>(decision.ProposalJson)!;
        var text = $"Просмотр #{decision.Id}, версия {decision.ReviewRevision}. Пока не сохранено.\n";
        var canAccept = !proposal.RequiresTargetSelection && !proposal.RequiresClarification;
        if (proposal.Kind == "confirm" && proposal.Changes.Count > 0)
        {
            foreach (var change in proposal.Changes) text += VetEventText.Describe(change.State) + "\n";
        }
        else if (proposal.Kind == "confirm")
        {
            var resolved = new List<VetEventChange>();
            var candidates = new List<VetCandidate>();
            foreach (var candidate in proposal.Candidates)
            {
                var validation = VetEventValidation.Validate(candidate with { Intent = "record" }, profile, source, decision.ExtractionResultId);
                candidates.Add(candidate);
                if (validation.State is { } state)
                {
                    if (proposal.ClarificationInputRevisionId is { } input && proposal.ClarificationResultId is { } result)
                        state = state with { InputRevisionId = input, ExtractionResultId = result };
                    resolved.Add(new(null, null, state));
                    text += VetEventText.Describe(state) + "\n";
                }
                else
                {
                    canAccept = false;
                    text += $"{candidate.EventType}: {candidate.RawValue ?? "значение не указано"} {candidate.Unit ?? "единица не указана"} — {validation.Reason}\n";
                }
            }
            // Freeze displayed candidates including runtime defaults before offering acceptance.
            if (canAccept && proposal.Candidates.Count > 0 && proposal.Changes.Count == 0)
            {
                var frozen = proposal with { Changes = resolved };
                if (await _diary.RevisePendingAsync(scope, decision.Id, decision.ReviewRevision, JsonSerializer.Serialize(frozen), ct))
                {
                    decision = (await _diary.GetPendingAsync(scope, decision.Id, ct))!;
                    proposal = frozen;
                    text = text.Replace($"версия {decision.ReviewRevision - 1}", $"версия {decision.ReviewRevision}");
                }
                else return;
            }
        }
        else foreach (var change in proposal.Changes)
            text += $"{(change.EventId is { } id ? "#" + id : "новый факт")}: "
                + (change.State.DeletedAt is not null ? "удалить " : "сохранить ") + VetEventText.Describe(change.State) + "\n";
        if (proposal.Reasons.Count > 0) text += string.Join("\n", proposal.Reasons) + "\n";
        if (proposal.RequiresTargetSelection || proposal.Changes.Count == 0 && proposal.Candidates.Count == 0) canAccept = false;
        text += canAccept ? "Подтвердите этот просмотр или отмените." : "Уточните недостающие данные ответом на этот просмотр или отмените.";
        if (text.Length > 3500 && !proposal.RequiresClarification)
        {
            // A partial preview is never proof that the full frozen subset was reviewed.
            var oversized = proposal with { RequiresClarification = true };
            if (!await _diary.RevisePendingAsync(scope, decision.Id, decision.ReviewRevision, JsonSerializer.Serialize(oversized), ct)) return;
            var oldRevision = decision.ReviewRevision;
            decision = (await _diary.GetPendingAsync(scope, decision.Id, ct))!;
            text = text.Replace($"версия {oldRevision}", $"версия {decision.ReviewRevision}");
            canAccept = false;
        }
        var promptId = await _replies.ReviewAsync(client, message, decision.Id, decision.ReviewRevision,
            text.Length <= 3500 ? text : text[..3300] + "\nПолный просмотр слишком велик: разбейте исходную запись.", canAccept && text.Length <= 3500, ct);
        if (promptId is { } prompt) await _diary.SetPromptAsync(scope, decision.Id, decision.ReviewRevision, prompt, ct);
    }
}
