using System.Globalization;
using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Messages;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoCommandReceiptStore
{
    private const int CommandRequestChars = 131072;
    private const int CommandPlanChars = 700000;
    private const int CommandStepChars = 8192;

    private static bool CommandScalar(string? value, int max) => value == null
        || value.Length <= max && ScalarText(value);

    private static bool CommandHumanContext(VetPhotoContext context) => context != null
        && context.CorrectionApproved && context.PreservedTime == null
        && CommandScalar(context.RawValue, 100) && CommandScalar(context.Unit, 40)
        && CommandScalar(context.Time, 8) && CommandScalar(context.Offset, 6)
        && (context.Year == null || context.Year is >= 1 and <= 9999)
        && (context.Month == null || context.Month is >= 1 and <= 12)
        && (context.Day == null || context.Day is >= 1 and <= 31)
        && (context.SelectedDisplayIndex == null || context.SelectedDisplayIndex is >= 0 and <= 7);

    private async Task<(VetTextSource Source, VetTextSourceRevision Revision)?> CommandSourceAsync(
        VetDiaryScope scope, Guid operation, long actor, CancellationToken ct)
    {
        if (operation == Guid.Empty || !await db.Bots.AnyAsync(b => b.Id == scope.BotDbId
            && b.FamilyId == scope.FamilyId && b.TelegramBotId == scope.TelegramBotId
            && b.Status == Assistant.Domain.Bots.BotStatus.Active, ct)) return null;
        var revision = await db.Set<VetTextSourceRevision>().AsNoTracking().SingleOrDefaultAsync(r =>
            r.FamilyId == scope.FamilyId && r.BotDbId == scope.BotDbId && r.OperationKey == operation, ct);
        var source = revision == null ? null : await Scoped<VetTextSource>(scope).AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == revision.SourceId && s.SourceSlot == 0 && s.SourceAuthorUserId == actor, ct);
        if (source?.SourceMessageDbId is not { } messageId || revision == null) return null;
        var bound = await db.Messages.AnyAsync(m => m.Id == messageId && m.FamilyId == scope.FamilyId
            && m.BotId == scope.TelegramBotId && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId
            && m.TelegramMessageId == source.TelegramMessageId && m.UserId == actor
            && m.Direction == MessageDirection.In && m.ChatType == source.ChatType && m.SentAt == source.SentAt
            && m.Kind != MessageKind.Service, ct);
        return bound ? (source, revision) : null;
    }

    private static bool ValidCommandStep(VetPhotoCommandStep step, VetDiaryScope scope, long actor)
    {
        if (step == null || JsonSerializer.Serialize(step, Json).Length > CommandStepChars) return false;
        var noPayload = step.Assumption == null && step.Human == null && step.Candidate == null && step.Late == null;
        return step.Kind switch
        {
            "start" => noPayload && step.BatchId == null && step.BatchRevision == null,
            "close" => noPayload && step.BatchId is { } id && id != Guid.Empty && step.BatchRevision is > 0,
            "assumption" => step.BatchId == null && step.BatchRevision == null && step.Human == null
                && step.Candidate == null && step.Late == null && step.Assumption is { } a && a.Scope == scope
                && a.ActorUserId == actor && a.BatchId != Guid.Empty && a.ExpectedBatchRevision > 0
                && a.ExpectedProfileRevision > 0 && Enum.IsDefined(a.Kind)
                && a.Value != null && CommandScalar(a.Value, 256),
            "human" => step.BatchId == null && step.BatchRevision == null && step.Assumption == null
                && step.Candidate == null && step.Late == null && step.Human is { } h && h.Scope == scope
                && h.ActorUserId == actor && h.BatchId != Guid.Empty && h.CandidateId != Guid.Empty
                && h.InputRevisionId != Guid.Empty && h.BatchRevision > 0 && h.CandidateRevision > 0
                && h.SourceOrdinal > 0 && CommandHumanContext(h.Context),
            "duplicate" => step.BatchId == null && step.BatchRevision == null && step.Assumption == null
                && step.Human == null && step.Late == null && step.Candidate is { } c && c.Scope == scope
                && c.ActorUserId == actor && c.BatchId != Guid.Empty && c.CandidateId != Guid.Empty
                && c.ExpectedCurrentInputId != Guid.Empty && c.ExpectedBatchRevision > 0
                && c.ExpectedCandidateRevision > 0 && c.ExpectedSourceOrdinal > 0
                && c.Kind is VetPhotoCandidateChangeKind.DuplicateExisting or VetPhotoCandidateChangeKind.DuplicateSeparate,
            "late" => step.BatchId == null && step.BatchRevision == null && step.Assumption == null
                && step.Human == null && step.Candidate == null && step.Late is { } l
                && l.BatchId != Guid.Empty && l.BatchRevision > 0 && l.SourceId != Guid.Empty && l.CurrentInputId != Guid.Empty,
            _ => false
        };
    }

    private static bool ValidCommandRequest(VetPhotoCommandPlanRequest request)
    {
        if (!WorkflowTextBound(request.RequestJson, CommandRequestChars) || request.RequestJson.Length == 0
            || request.Steps == null || request.Steps.Count is < 1 or > 64
            || request.Steps.Any(s => !ValidCommandStep(s, request.Scope, request.ActorUserId))) return false;
        try
        {
            using var document = JsonDocument.Parse(request.RequestJson, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        }
        catch (JsonException) { return false; }
        var candidateIds = request.Steps.Select(s => s.Human?.CandidateId ?? s.Candidate?.CandidateId)
            .Where(id => id != null).ToArray();
        var lateIds = request.Steps.Where(s => s.Late != null).Select(s => s.Late!.SourceId).ToArray();
        return candidateIds.Length + lateIds.Length <= 50
            && candidateIds.Distinct().Count() == candidateIds.Length
            && lateIds.Distinct().Count() == lateIds.Length;
    }

    private static VetPhotoCommandPlan? ReadCommandPlan(VetPhotoReview row, VetDiaryScope scope, long actor)
    {
        if (row.Kind != "command_plan" || row.State != "command_ready" || row.RequesterUserId != actor
            || row.PageCount != 0 || row.CompletePreviewDelivered || row.AcceptancePromptMessageId != null
            || row.ActionId != null || !WorkflowTextBound(row.SelectionJson, CommandPlanChars)
            || row.Fingerprint != Hash(Encoding.UTF8.GetBytes(row.SelectionJson))) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<VetPhotoCommandPlan>(row.SelectionJson,
                new JsonSerializerOptions(Json) { MaxDepth = 16 });
            return plan != null && plan.Id == row.Id && plan.Scope == scope && plan.ActorUserId == actor
                && plan.SourceOperationKey == row.OperationKey && plan.SourceId != Guid.Empty && plan.SourceRevisionId != Guid.Empty
                && ValidCommandRequest(new(scope, plan.SourceOperationKey, actor, plan.RequestJson, plan.Steps)) ? plan : null;
        }
        catch (JsonException) { return null; }
    }

    public Task<VetPhotoCommandPlanResult> ReadPlanAsync(VetDiaryScope scope, Guid sourceOperationKey,
        long actorUserId, CancellationToken ct) => WorkflowAsync(scope, actorUserId,
        new VetPhotoCommandPlanResult(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            if (await CommandSourceAsync(scope, sourceOperationKey, actorUserId, ct) == null)
                return new(VetPhotoWorkflowStatus.Refused, null);
            var row = await Scoped<VetPhotoReview>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.OperationKey == sourceOperationKey, ct);
            if (row == null) return new(VetPhotoWorkflowStatus.NotFound, null);
            var plan = ReadCommandPlan(row, scope, actorUserId);
            return new(plan == null ? VetPhotoWorkflowStatus.Refused : VetPhotoWorkflowStatus.Existing, plan);
        }, ct);

    public Task<VetPhotoCommandPlanResult> PreparePlanAsync(VetPhotoCommandPlanRequest request,
        CancellationToken ct) => WorkflowAsync(request.Scope, request.ActorUserId,
        new VetPhotoCommandPlanResult(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            if (!ValidCommandRequest(request)) return new(VetPhotoWorkflowStatus.Refused, null);
            var source = await CommandSourceAsync(request.Scope, request.SourceOperationKey, request.ActorUserId, ct);
            if (source == null) return new(VetPhotoWorkflowStatus.Refused, null);
            var prior = await Scoped<VetPhotoReview>(request.Scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.OperationKey == request.SourceOperationKey, ct);
            if (prior != null)
            {
                var old = ReadCommandPlan(prior, request.Scope, request.ActorUserId);
                return old != null && old.RequestJson == request.RequestJson && old.SourceId == source.Value.Source.Id
                    && old.SourceRevisionId == source.Value.Revision.Id
                    ? new(VetPhotoWorkflowStatus.Existing, old) : new(VetPhotoWorkflowStatus.Refused, null);
            }
            if (source.Value.Source.CurrentInputRevisionId != source.Value.Revision.Id)
                return new(VetPhotoWorkflowStatus.Stale, null);
            var plan = new VetPhotoCommandPlan(Guid.NewGuid(), request.Scope, request.SourceOperationKey,
                source.Value.Source.Id, source.Value.Revision.Id, request.ActorUserId, request.RequestJson, request.Steps);
            var encoded = JsonSerializer.Serialize(plan, Json);
            if (!WorkflowTextBound(encoded, CommandPlanChars)) return new(VetPhotoWorkflowStatus.Refused, null);
            db.Add(InScope(new VetPhotoReview { Id = plan.Id, OperationKey = request.SourceOperationKey,
                Kind = "command_plan", State = "command_ready", RequesterUserId = request.ActorUserId,
                SelectionJson = encoded, Fingerprint = Hash(Encoding.UTF8.GetBytes(encoded)), CreatedAt = clock.UtcNow }, request.Scope));
            return new(VetPhotoWorkflowStatus.Applied, plan);
        }, ct);

    private static Guid CommandStepKey(Guid operation, int index) => new(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes("photo-command:" + operation.ToString("D") + ":" + index.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));

    private static string CommandStepSelection(VetPhotoCommandPlan plan, int index) =>
        JsonSerializer.Serialize(new { plan.Id, plan.SourceOperationKey, plan.SourceId,
            plan.SourceRevisionId, index, step = plan.Steps[index] }, Json);

    private static VetPhotoCommandStepOutcome? ReadCommandReceipt(VetPhotoReview row,
        VetPhotoCommandPlan plan, int index, long actor)
    {
        var expected = CommandStepSelection(plan, index);
        if (row.Kind != "command_receipt" || row.State != "command_done" || row.RequesterUserId != actor
            || row.DecisionActorUserId != actor || row.SelectionJson != expected
            || row.Fingerprint != Hash(Encoding.UTF8.GetBytes(expected)) || row.OutcomeJson == null
            || !WorkflowTextBound(row.OutcomeJson, 4096) || row.PageCount != 0 || row.CompletePreviewDelivered
            || row.ActionId != null || row.AcceptancePromptMessageId != null) return null;
        try
        {
            using var document = JsonDocument.Parse(row.OutcomeJson, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            string[] fields = ["status", "batchId", "candidateId", "extractionResultId"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length
                || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
                || root.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number
                || !status.TryGetInt32(out _)) return null;
            var outcome = JsonSerializer.Deserialize<VetPhotoCommandStepOutcome>(row.OutcomeJson, Json);
            if (outcome == null || !Enum.IsDefined(outcome.Status) || outcome.BatchId == Guid.Empty
                || outcome.CandidateId == Guid.Empty || outcome.ExtractionResultId == Guid.Empty) return null;
            var step = plan.Steps[index];
            if (outcome.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing)) return outcome;
            var valid = step.Kind switch
            {
                "start" => outcome.BatchId != null && outcome.CandidateId == null && outcome.ExtractionResultId == null,
                "close" => outcome.BatchId == step.BatchId && outcome.CandidateId == null && outcome.ExtractionResultId == null,
                "assumption" => outcome.BatchId == step.Assumption!.BatchId && outcome.CandidateId == null && outcome.ExtractionResultId == null,
                "late" => outcome.BatchId == step.Late!.BatchId && outcome.CandidateId == null && outcome.ExtractionResultId == null,
                "duplicate" => outcome.BatchId == step.Candidate!.BatchId && outcome.CandidateId == step.Candidate.CandidateId
                    && outcome.ExtractionResultId == null,
                "human" => outcome.BatchId == step.Human!.BatchId && outcome.CandidateId == step.Human.CandidateId
                    && outcome.ExtractionResultId != null,
                _ => false
            };
            return valid ? outcome : null;
        }
        catch (JsonException) { return null; }
    }

    public Task<VetPhotoCommandStepOutcome> ExecuteStepAsync(VetDiaryScope scope, Guid sourceOperationKey,
        int stepIndex, long actorUserId, CancellationToken ct) => WorkflowAsync(scope, actorUserId,
        new VetPhotoCommandStepOutcome(VetPhotoWorkflowStatus.Refused), async () =>
        {
            var source = await CommandSourceAsync(scope, sourceOperationKey, actorUserId, ct);
            var row = await Scoped<VetPhotoReview>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.OperationKey == sourceOperationKey, ct);
            var plan = row == null ? null : ReadCommandPlan(row, scope, actorUserId);
            if (source == null || plan == null || plan.SourceId != source.Value.Source.Id
                || plan.SourceRevisionId != source.Value.Revision.Id || stepIndex < 0 || stepIndex >= plan.Steps.Count)
                return new(VetPhotoWorkflowStatus.Refused);
            var step = plan.Steps[stepIndex];
            var selection = CommandStepSelection(plan, stepIndex);
            var key = CommandStepKey(sourceOperationKey, stepIndex);
            var prior = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.OperationKey == key, ct);
            if (prior != null)
            {
                return ReadCommandReceipt(prior, plan, stepIndex, actorUserId) ?? new(VetPhotoWorkflowStatus.Refused);
            }
            if (source.Value.Source.CurrentInputRevisionId != plan.SourceRevisionId)
                return new(VetPhotoWorkflowStatus.Stale);
            var priorKeys = Enumerable.Range(0, stepIndex).Select(i => CommandStepKey(sourceOperationKey, i)).ToArray();
            var completed = await Scoped<VetPhotoReview>(scope).AsNoTracking()
                .Where(r => priorKeys.Contains(r.OperationKey)).ToListAsync(ct);
            if (completed.Count != stepIndex) return new(VetPhotoWorkflowStatus.Incomplete);
            for (var index = 0; index < stepIndex; index++)
            {
                var receipt = completed.SingleOrDefault(r => r.OperationKey == priorKeys[index]);
                var old = receipt == null ? null : ReadCommandReceipt(receipt, plan, index, actorUserId);
                if (old?.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
                    return new(VetPhotoWorkflowStatus.Stale);
            }
            // Execute in the same capacity-before-bot transaction as the receipt. No nested workflow transaction.
            VetPhotoCommandStepOutcome outcome;
            if (step.Kind == "start")
            {
                var result = await StartCollectionCoreAsync(scope, actorUserId, ct);
                outcome = new(result.Status, result.Batch?.Id);
            }
            else if (step.Kind == "close")
            {
                var result = await CloseCollectionCoreAsync(scope, step.BatchId!.Value, step.BatchRevision!.Value, actorUserId, ct);
                outcome = new(result.Status, result.Batch?.Id);
            }
            else if (step.Kind == "assumption")
            {
                var result = await ChangeAssumptionCoreAsync(step.Assumption!, ct);
                outcome = new(result.Status, result.Batch?.Id);
            }
            else if (step.Kind == "late")
            {
                var late = step.Late!;
                var result = await AddLateSourceCoreAsync(scope, late.BatchId, late.BatchRevision, late.SourceId, late.CurrentInputId, actorUserId, ct);
                outcome = new(result.Status, result.Batch?.Id);
            }
            else if (step.Kind == "duplicate")
            {
                var change = step.Candidate!;
                outcome = new(await ChangeCandidateCoreAsync(change, ct), change.BatchId, change.CandidateId);
            }
            else if (step.Kind == "human")
            {
                var human = step.Human!;
                var status = await ProposeHumanCorrectionCoreAsync(human, ct);
                await db.SaveChangesAsync(ct);
                var resultId = await Scoped<VetPhotoCandidate>(scope).Where(c => c.Id == human.CandidateId)
                    .Select(c => c.ExtractionResultId).SingleOrDefaultAsync(ct);
                outcome = new(status, human.BatchId, human.CandidateId, resultId);
            }
            else return new(VetPhotoWorkflowStatus.Refused);
            var encodedOutcome = JsonSerializer.Serialize(outcome, Json);
            db.Add(InScope(new VetPhotoReview { Id = Guid.NewGuid(), OperationKey = key, Kind = "command_receipt",
                State = "command_done", RequesterUserId = actorUserId, DecisionActorUserId = actorUserId,
                SelectionJson = selection, Fingerprint = Hash(Encoding.UTF8.GetBytes(selection)),
                OutcomeJson = encodedOutcome, CreatedAt = clock.UtcNow, DecidedAt = clock.UtcNow }, scope));
            return outcome;
        }, ct, capacityLock: true);
}
