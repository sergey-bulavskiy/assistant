using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoPresentationStore
{
    public async Task<VetPhotoPresentationEvidence?> ReadEvidenceAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, Guid? extractionResultId, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await ImageActorAsync(scope, actorUserId, ct)) return null;
        var source = await ImageSourceAsync(scope, sourceId, ct);
        if (source == null) return null;
        var input = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == inputRevisionId && i.SourceId == sourceId, ct);
        if (input == null) return null;
        var candidate = await Scoped<VetPhotoCandidate>(scope).AsNoTracking()
            .SingleAsync(c => c.SourceId == sourceId && c.CandidateOrdinal == 0, ct);
        var selected = extractionResultId ?? (candidate.InputRevisionId == inputRevisionId ? candidate.ExtractionResultId : null);
        var extraction = selected == null ? null : await Scoped<VetPhotoExtraction>(scope).AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == selected && e.SourceId == sourceId && e.InputRevisionId == inputRevisionId, ct);
        if (extractionResultId == null && extraction == null)
            extraction = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().Where(e => e.SourceId == sourceId
                && e.InputRevisionId == inputRevisionId && (e.State == "returned" || e.State == "human" || e.State == "invalid" || e.State == "comparison"))
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        var original = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking()
            .SingleOrDefaultAsync(r => r.InputRevisionId == inputRevisionId, ct);
        var caption = await VetPhotoCaptionReader.ReadAsync(db, scope, source, input, ct);
        var ownedEvent = candidate.EventId == null ? null : await db.Set<VetEvent>().AsNoTracking().SingleOrDefaultAsync(e =>
            e.Id == candidate.EventId && e.FamilyId == scope.FamilyId && e.BotDbId == scope.BotDbId
            && e.TelegramBotId == scope.TelegramBotId && e.ChatId == scope.ChatId && e.TopicId == scope.TopicId, ct);
        return new(source, input, candidate, extraction, original, caption, ownedEvent);
    }

    public Task<VetPhotoBatchChange> RefreshProfileSnapshotAsync(VetDiaryScope scope, Guid batchId,
        int expectedBatchRevision, int profileRevision, long actorUserId, CancellationToken ct) =>
        WorkflowAsync(scope, actorUserId, new VetPhotoBatchChange(VetPhotoWorkflowStatus.Refused, null), async () =>
        {
            if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoWorkflowStatus.Refused, null);
            var batch = await BatchAsync(scope, batchId, ct);
            var profile = await WorkflowProfileAsync(scope, ct);
            if (batch == null) return new(VetPhotoWorkflowStatus.NotFound, null);
            if (batch.ReviewRevision != expectedBatchRevision || profile == null || profile.Revision != profileRevision
                || profile.Id != batch.ProfileId) return new(VetPhotoWorkflowStatus.Stale, batch);
            var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, Json)!;
            var fresh = assumptions with { ProfileTimeZone = profile.TimeZone, ProfileGlucoseUnit = profile.GlucoseUnit };
            var encoded = JsonSerializer.Serialize(fresh, Json);
            if (batch.ProfileRevision == profile.Revision && encoded == batch.AssumptionsJson)
                return new(VetPhotoWorkflowStatus.Existing, batch);
            db.Attach(batch); batch.ProfileRevision = profile.Revision; batch.AssumptionsJson = encoded;
            await TouchBatchAsync(scope, batch, ct);
            return new(VetPhotoWorkflowStatus.Applied, batch);
        }, ct);

    public Task<VetPhotoWorkflowStatus> SetValidationAsync(VetPhotoValidationUpdate update, CancellationToken ct) =>
        WorkflowAsync(update.Scope, update.ActorUserId, VetPhotoWorkflowStatus.Refused, async () =>
        {
            if (!await ImageActorAsync(update.Scope, update.ActorUserId, ct)) return VetPhotoWorkflowStatus.Refused;
            var batch = await BatchAsync(update.Scope, update.BatchId, ct);
            var source = await ImageSourceAsync(update.Scope,
                await Scoped<VetPhotoCandidate>(update.Scope).Where(c => c.Id == update.CandidateId).Select(c => c.SourceId).FirstOrDefaultAsync(ct), ct);
            var candidate = await Scoped<VetPhotoCandidate>(update.Scope).AsNoTracking().SingleOrDefaultAsync(c => c.Id == update.CandidateId, ct);
            if (batch == null || source == null || candidate == null || batch.ReviewRevision != update.BatchRevision
                || candidate.Revision != update.CandidateRevision || candidate.BatchId != batch.Id
                || source.CurrentInputRevisionId != update.InputRevisionId || candidate.InputRevisionId != update.InputRevisionId
                || candidate.ExtractionResultId != update.ExtractionResultId || batch.ProfileRevision != update.ProfileRevision)
                return VetPhotoWorkflowStatus.Stale;
            if (candidate.EventId != null || candidate.ManuallyCorrected || candidate.RequiresExplicitRestoration
                || candidate.State is "linked" or "excluded" or "cancelled" or "deleted") return VetPhotoWorkflowStatus.Refused;
            var extraction = await Scoped<VetPhotoExtraction>(update.Scope).AsNoTracking().SingleOrDefaultAsync(e =>
                e.Id == update.ExtractionResultId && e.InputRevisionId == update.InputRevisionId && e.SourceId == source.Id && e.State == "returned", ct);
            var input = await Scoped<VetPhotoInputRevision>(update.Scope).AsNoTracking().SingleOrDefaultAsync(i => i.Id == update.InputRevisionId, ct);
            var image = extraction == null ? null : VetPhotoInterpretationParser.Parse(extraction.StructuredJson, source.Id, update.InputRevisionId);
            if (image == null || input == null || update.Context.CorrectionApproved) return VetPhotoWorkflowStatus.Refused;
            var captionEvidence = await VetPhotoCaptionReader.ReadAsync(db, update.Scope, source, input, ct);
            var (expectedContext, captionFailure) = VetPhotoCaptionContext.Read(captionEvidence);
            if (update.Context != expectedContext) return VetPhotoWorkflowStatus.Refused;
            if (captionFailure is "ambiguous_caption_readings" or "uncertain_caption_reading"
                or "caption_date_requires_clarification" or "caption_processing" && update.Validation.Effective != null)
                return VetPhotoWorkflowStatus.Refused;
            var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, Json)!;
            var validated = VetPhotoValidationRules.Validate(image, update.Context, assumptions, input.ReceivedAt);
            // Caller may add a caption-ambiguity reason, but may never strengthen a failed validation into a fact.
            if (update.Validation.Effective != null && update.Validation.Effective != validated.Effective)
                return VetPhotoWorkflowStatus.Refused;
            var effective = JsonSerializer.Serialize(update.Validation.Effective, Json);
            var reasons = JsonSerializer.Serialize(update.Validation.Reasons, Json);
            if (update.Validation.Reasons.Count > 20 || update.Validation.Reasons.Any(r => r.Length > 500 || !ScalarText(r)))
                return VetPhotoWorkflowStatus.Refused;
            var context = JsonSerializer.Serialize(update.Context, Json);
            var state = update.Validation.Effective == null ? "pending" : "clear";
            if (candidate.State == state && candidate.EffectiveJson == effective && candidate.ReasonsJson == reasons
                && candidate.CorrectionProvenanceJson == context) return VetPhotoWorkflowStatus.Existing;
            db.Attach(candidate); candidate.State = state; candidate.EffectiveJson = effective; candidate.ReasonsJson = reasons;
            candidate.CorrectionProvenanceJson = context; candidate.Revision++; candidate.UpdatedAt = clock.UtcNow;
            db.Attach(batch); await TouchBatchAsync(update.Scope, batch, ct);
            return VetPhotoWorkflowStatus.Applied;
        }, ct);

    public Task<VetPhotoWorkflowStatus> ProposeHumanCorrectionAsync(VetPhotoHumanProposal proposal, CancellationToken ct) =>
        WorkflowAsync(proposal.Scope, proposal.ActorUserId, VetPhotoWorkflowStatus.Refused,
            () => ProposeHumanCorrectionCoreAsync(proposal, ct), ct, capacityLock: true);

    private async Task<VetPhotoWorkflowStatus> ProposeHumanCorrectionCoreAsync(VetPhotoHumanProposal proposal, CancellationToken ct)
    {
            if (!await ImageActorAsync(proposal.Scope, proposal.ActorUserId, ct)) return VetPhotoWorkflowStatus.Refused;
            if (!proposal.Context.CorrectionApproved) return VetPhotoWorkflowStatus.Refused;
            var batch = await BatchAsync(proposal.Scope, proposal.BatchId, ct);
            var candidate = await Scoped<VetPhotoCandidate>(proposal.Scope).AsNoTracking().SingleOrDefaultAsync(c => c.Id == proposal.CandidateId, ct);
            var source = candidate == null ? null : await ImageSourceAsync(proposal.Scope, candidate.SourceId, ct);
            if (batch == null || candidate == null || source == null || candidate.BatchId != batch.Id
                || batch.ReviewRevision != proposal.BatchRevision || candidate.Revision != proposal.CandidateRevision
                || source.CurrentInputRevisionId != proposal.InputRevisionId || source.CurrentOrdinal != proposal.SourceOrdinal)
                return VetPhotoWorkflowStatus.Stale;
            var input = await Scoped<VetPhotoInputRevision>(proposal.Scope).AsNoTracking().SingleAsync(i => i.Id == proposal.InputRevisionId, ct);
            var old = candidate.ExtractionResultId == null ? null : await Scoped<VetPhotoExtraction>(proposal.Scope).AsNoTracking()
                .SingleOrDefaultAsync(e => e.Id == candidate.ExtractionResultId && e.InputRevisionId == input.Id, ct);
            var oldImage = old == null ? null : VetPhotoInterpretationParser.Parse(old.StructuredJson, source.Id, input.Id);
            var context = proposal.Context;
            if (context.PreservedTime != null) return VetPhotoWorkflowStatus.Refused;
            if (candidate.EventId is { } eventId && context.Year == null && context.Month == null && context.Day == null
                && context.Time == null && context.Offset == null)
            {
                var saved = await db.Set<VetEvent>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId
                    && e.FamilyId == proposal.Scope.FamilyId && e.BotDbId == proposal.Scope.BotDbId
                    && e.TelegramBotId == proposal.Scope.TelegramBotId && e.ChatId == proposal.Scope.ChatId
                    && e.TopicId == proposal.Scope.TopicId && e.Revision == candidate.EventRevision, ct);
                if (saved == null) return VetPhotoWorkflowStatus.Stale;
                context = context with { PreservedTime = new(saved.OccurredAt, saved.LocalTime, saved.TimeZoneSnapshot, saved.OccurredAtSource) };
            }
            if (oldImage == null && (context.RawValue == null || context.Unit == null || context.PreservedTime == null
                && (context.Year == null || context.Month == null || context.Day == null || context.Time == null)))
                return VetPhotoWorkflowStatus.Refused;
            var display = oldImage?.Displays.FirstOrDefault() ?? new VetPhotoDisplay(context.RawValue!,
                VetInterpretationParser.TryPositiveDecimal(context.RawValue, out var value) ? value : null,
                context.Unit, context.Year, context.Year != null, context.Month, context.Day, context.Time, context.Offset);
            IReadOnlyList<VetPhotoDisplay> displays = oldImage?.Displays.Count > 0 ? oldImage.Displays : [display];
            if (displays.Count > 1 && context.SelectedDisplayIndex == null)
            {
                var matches = displays.Select((d, index) => (d, index)).Where(x =>
                    context.RawValue != null && VetInterpretationParser.TryPositiveDecimal(context.RawValue, out var desired)
                    && x.d.NumericValue == desired && (context.Year == null || x.d.Year == null || x.d.Year == context.Year)
                    && (context.Month == null || x.d.Month == null || x.d.Month == context.Month)
                    && (context.Day == null || x.d.Day == null || x.d.Day == context.Day)
                    && (context.Time == null || x.d.Time == null || x.d.Time == context.Time)).ToArray();
                if (matches.Length == 1) context = context with { SelectedDisplayIndex = matches[0].index };
                else if (context.RawValue != null && context.Unit != null && context.Year != null
                    && context.Month != null && context.Day != null && context.Time != null)
                {
                    displays = [new(context.RawValue, VetInterpretationParser.TryPositiveDecimal(context.RawValue, out var desired) ? desired : null,
                        context.Unit, context.Year, true, context.Month, context.Day, context.Time, context.Offset)];
                    context = context with { SelectedDisplayIndex = 0 };
                }
                else return VetPhotoWorkflowStatus.Refused;
            }
            var human = new VetPhotoInterpretation(source.Id, input.Id, "meter", displays, [], null);
            var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, Json)!;
            var validation = VetPhotoValidationRules.Validate(human, context, assumptions, input.ReceivedAt);
            if (!(await TotalsLockedAsync(ct)).CanReserve(capacity, 0, false, true)) return VetPhotoWorkflowStatus.Full;
            var structured = JsonSerializer.Serialize(new { schema_version = 1, photo_source_id = source.Id,
                input_revision_id = input.Id, kind = "meter", displays = displays.Select(d => new { value_text = d.ValueText,
                    decimal_value = d.NumericValue, unit = d.Unit, year = d.Year, year_displayed = d.YearDisplayed,
                    month = d.Month, day = d.Day, time = d.Time, offset = d.Offset }).ToArray(), reasons = Array.Empty<string>(), notes = (string?)null }, Json);
            if (VetPhotoInterpretationParser.Parse(structured, source.Id, input.Id) == null) return VetPhotoWorkflowStatus.Refused;
            var attempt = InScope(new VetPhotoAttempt { Id = Guid.NewGuid(), SourceId = source.Id, InputRevisionId = input.Id,
                ActorUserId = proposal.ActorUserId, Kind = "human", State = "returned", ExpectedCurrentInputId = input.Id,
                ExpectedSourceOrdinal = source.CurrentOrdinal, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow }, proposal.Scope);
            var extraction = InScope(new VetPhotoExtraction { Id = Guid.NewGuid(), SourceId = source.Id, InputRevisionId = input.Id,
                AttemptId = attempt.Id, ModelName = "human", State = "human", StructuredJson = structured, CreatedAt = clock.UtcNow }, proposal.Scope);
            attempt.ExtractionResultId = extraction.Id; db.Add(attempt); db.Add(extraction);
            await db.SaveChangesAsync(ct);
            db.Attach(candidate); candidate.InputRevisionId = input.Id; candidate.ExtractionResultId = extraction.Id;
            candidate.ManuallyCorrected = true; candidate.State = "pending"; candidate.Revision++;
            if (proposal.RestoreRequested) candidate.RequiresExplicitRestoration = true;
            candidate.CorrectionProvenanceJson = JsonSerializer.Serialize(context, Json);
            candidate.EffectiveJson = JsonSerializer.Serialize(validation.Effective, Json);
            candidate.ReasonsJson = JsonSerializer.Serialize(validation.Reasons, Json); candidate.UpdatedAt = clock.UtcNow;
            db.Attach(batch); await TouchBatchAsync(proposal.Scope, batch, ct);
            return VetPhotoWorkflowStatus.Applied;
    }

    public Task<VetPhotoWorkflowStatus> SetProgressMessageAsync(VetDiaryScope scope, Guid batchId,
        long actorUserId, int messageId, CancellationToken ct) => WorkflowAsync(scope, actorUserId, VetPhotoWorkflowStatus.Refused, async () =>
        {
            if (!await ImageActorAsync(scope, actorUserId, ct)) return VetPhotoWorkflowStatus.Refused;
            var batch = await BatchAsync(scope, batchId, ct);
            if (batch == null || messageId <= 0) return VetPhotoWorkflowStatus.NotFound;
            db.Attach(batch); batch.ProgressMessageId = messageId; batch.UpdatedAt = clock.UtcNow;
            return VetPhotoWorkflowStatus.Applied;
        }, ct);

    public async Task<VetPhotoReview?> ReadPreviewAsync(VetDiaryScope scope, Guid reviewId, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        return !await ImageActorAsync(scope, actorUserId, ct) ? null : await Scoped<VetPhotoReview>(scope).AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == reviewId, ct);
    }

    public Task<VetPhotoWorkflowStatus> DeclineReviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) =>
        WorkflowAsync(handle.Scope, handle.ActorUserId, VetPhotoWorkflowStatus.Refused, async () =>
        {
            if (!await ImageActorAsync(handle.Scope, handle.ActorUserId, ct)) return VetPhotoWorkflowStatus.Refused;
            var review = await ExactPreviewAsync(handle, ct);
            if (review?.Kind == "evidence") return VetPhotoWorkflowStatus.Refused;
            if (review == null || !ReviewDelivered(review)) return VetPhotoWorkflowStatus.Stale;
            db.Attach(review); review.State = "declined"; review.DecisionActorUserId = handle.ActorUserId; review.DecidedAt = clock.UtcNow;
            return VetPhotoWorkflowStatus.Applied;
        }, ct);

    public async Task<IReadOnlyList<VetPhotoRecoverableBatch>> GetRecoverableBatchesAsync(long familyId, long botDbId, int limit, CancellationToken ct)
    {
        _guard.Family(familyId); ForgetPhotoSnapshots(); await _guard.BotAsync(familyId, botDbId, null, ct);
        if (limit is < 1 or > 5) throw new InvalidOperationException("Photo batch limit is invalid.");
        var bot = await db.Bots.AsNoTracking().SingleOrDefaultAsync(b => b.Id == botDbId && b.FamilyId == familyId && b.Status == BotStatus.Active, ct);
        if (bot == null) return [];
        var batches = await db.Set<VetPhotoBatch>().AsNoTracking().Where(b => b.FamilyId == familyId && b.BotDbId == botDbId
            && b.TelegramBotId == bot.TelegramBotId && (b.State == "closed" || b.State == "completed"
                && db.Set<VetPhotoSource>().Any(s => s.BatchId == b.Id && s.FamilyId == familyId && s.BotDbId == botDbId
                    && s.TelegramBotId == bot.TelegramBotId && s.ChatId == b.ChatId && s.TopicId == b.TopicId
                    && db.Set<VetPhotoCandidate>().Any(c => c.SourceId == s.Id && c.FamilyId == familyId && c.BotDbId == botDbId
                        && c.TelegramBotId == bot.TelegramBotId && c.ChatId == b.ChatId && c.TopicId == b.TopicId
                        && c.InputRevisionId != s.CurrentInputRevisionId)))
            // Metadata/results only: waiting transports and already shown current notices cannot occupy recovery.
            && db.Set<VetPhotoSource>().Any(s => s.BatchId == b.Id && s.FamilyId == familyId && s.BotDbId == botDbId
                && s.TelegramBotId == bot.TelegramBotId && s.ChatId == b.ChatId && s.TopicId == b.TopicId
                && db.Set<VetPhotoExtraction>().Any(e => e.SourceId == s.Id && e.InputRevisionId == s.CurrentInputRevisionId
                    && e.FamilyId == familyId && e.BotDbId == botDbId && e.TelegramBotId == bot.TelegramBotId
                    && e.ChatId == b.ChatId && e.TopicId == b.TopicId
                    && (e.State == "returned" || e.State == "human" || e.State == "invalid" || e.State == "comparison")))
            && !db.Set<VetPhotoReview>().Any(r => r.BatchId == b.Id && r.FamilyId == familyId && r.BotDbId == botDbId
                && r.TelegramBotId == bot.TelegramBotId && r.ChatId == b.ChatId && r.TopicId == b.TopicId
                && r.BatchReviewRevision == b.ReviewRevision && r.ProfileId == b.ProfileId && r.ProfileRevision == b.ProfileRevision
                && db.Set<VetProfile>().Any(p => p.Id == r.ProfileId && p.FamilyId == familyId && p.BotDbId == botDbId
                    && p.Revision == r.ProfileRevision)
                && (r.Kind == "evidence" || r.Kind == "save" || r.Kind == "correction")
                && (r.State == "preview_failed" || r.State == "preview" && r.CompletePreviewDelivered && r.AcceptancePromptMessageId != null))
            && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == b.StarterUserId && m.Status == FamilyMemberStatus.Approved)
            && (b.ChatId == b.StarterUserId && b.TopicId == null && db.Set<VetPhotoSource>().Any(s => s.BatchId == b.Id
                && s.FamilyId == familyId && s.BotDbId == botDbId && s.TelegramBotId == bot.TelegramBotId
                && s.ChatId == b.ChatId && s.TopicId == null && s.ChatType == "private")
                || db.Places.Any(p => p.BotId == botDbId && p.ChatId == b.ChatId && p.TopicId == b.TopicId
                    && p.Status == Assistant.Domain.Places.PlaceStatus.Approved)))
            .OrderBy(b => b.UpdatedAt).ThenBy(b => b.Id).Take(limit * 2).ToListAsync(ct);
        var found = new List<VetPhotoRecoverableBatch>();
        foreach (var batch in batches)
        {
            var scope = new VetDiaryScope(familyId, botDbId, bot.TelegramBotId, batch.ChatId, batch.TopicId);
            if (await ImageActorAsync(scope, batch.StarterUserId, ct)) found.Add(new(scope, batch.Id, batch.StarterUserId));
            if (found.Count == limit) break;
        }
        return found;
    }
}
