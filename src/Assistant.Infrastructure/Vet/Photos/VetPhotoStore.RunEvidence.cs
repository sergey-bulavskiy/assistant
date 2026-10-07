using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoRunEvidenceStore, IVetPhotoRunReviewSelectionStore
{
    private async Task<(VetPhotoRun Run,VetPhotoRunWindow Window,VetPhotoReview Approval,VetPhotoRunInputSnapshot[] Inputs)?>
        ReadWindowProofLockedAsync(VetPhotoRunHandle handle,Guid windowId,CancellationToken ct)
    {
        if(!await ImageActorAsync(handle.Scope,handle.ActorUserId,ct))return null;
        var run=await Scoped<VetPhotoRun>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==handle.RunId,ct);
        var window=await Scoped<VetPhotoRunWindow>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(w=>w.Id==windowId&&w.RunId==handle.RunId,ct);
        if(run==null||window==null||DeletionRun(run)||run.ModelName!="gpt-6.1-sol")return null;
        var approval=await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==run.SelectionReviewId,ct);
        var profile=await WorkflowProfileAsync(handle.Scope,ct);
        if(approval==null||profile==null||approval.ProfileId!=profile.Id||approval.ProfileRevision is not >0
            ||approval.State!="accepted"||approval.Kind!="reextract_selection"
            ||approval.DecisionActorUserId!=run.ActorUserId||approval.SelectionJson!=run.SelectionJson||!ReviewDelivered(approval))return null;
        var all=ReadRunSelection<VetPhotoRunInputSnapshot>(run.SelectionJson,capacity.MaxInputRevisions,768);
        var selected=ReadRunSelection<VetPhotoRunInputSnapshot>(window.SelectionJson,50,768);
        if(all==null||selected==null||all.Length!=run.SelectedCount||all.Select(s=>s.AttemptKey).Distinct().Count()!=all.Length
            ||all.Select(s=>s.InputRevisionId).Distinct().Count()!=all.Length||window.Ordinal<0
            ||!selected.SequenceEqual(all.Skip(window.Ordinal*50).Take(50)))return null;
        return(run,window,approval,selected);
    }

    public Task<VetPhotoRunEvidence?> ReadWindowAsync(VetPhotoRunHandle handle,Guid windowId,CancellationToken ct)=>
        WorkflowAsync<VetPhotoRunEvidence?>(handle.Scope,handle.ActorUserId,null,async()=>
        {
            var proof=await ReadWindowProofLockedAsync(handle,windowId,ct);if(proof==null)return null;
            var p=proof.Value;var items=new List<VetPhotoRunEvidenceItem>();
            foreach(var snapshot in p.Inputs)
            {
                var attempt=await Scoped<VetPhotoAttempt>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(a=>a.Id==snapshot.AttemptKey,ct);
                if(attempt!=null&&(attempt.RunWindowId!=p.Window.Id||attempt.SourceId!=snapshot.SourceId||attempt.InputRevisionId!=snapshot.InputRevisionId
                    ||attempt.Kind!="image"||attempt.ActorUserId!=p.Run.ActorUserId||attempt.ExpectedCurrentInputId!=snapshot.ExpectedCurrentInputId
                    ||attempt.ExpectedSourceOrdinal!=snapshot.ExpectedSourceOrdinal))return null;
                var evidence=await ReadEvidenceAsync(handle.Scope,snapshot.SourceId,snapshot.InputRevisionId,attempt?.ExtractionResultId,handle.ActorUserId,ct);
                if(evidence==null||evidence.Original?.Id!=snapshot.OriginalReferenceId
                    ||attempt?.ExtractionResultId!=null&&(evidence.Extraction?.Id!=attempt.ExtractionResultId||evidence.Extraction.AttemptId!=attempt.Id))return null;
                if(p.Window.State is not ("completed" or "cancelled")&&(evidence.Source.CurrentInputRevisionId!=snapshot.ExpectedCurrentInputId
                    ||evidence.Source.CurrentOrdinal!=snapshot.ExpectedSourceOrdinal||evidence.Original.Revision!=snapshot.ExpectedReferenceRevision
                    ||snapshot.ExpectedCandidateRevision is { } revision&&evidence.Candidate.Revision!=revision))return null;
                // A absent result is not allowed to fall back to a different prior extraction for this input.
                if(attempt?.ExtractionResultId==null)evidence=evidence with{Extraction=null};
                items.Add(new(snapshot,attempt,evidence));
            }
            return new(p.Run,p.Window,p.Approval,items);
        },ct);

    public async Task<VetPhotoRunReviewOwner?> FindReviewRunAsync(VetDiaryScope scope,Guid reviewId,long actor,CancellationToken ct)
    {
        await CheckAsync(scope,ct);if(!await ImageActorAsync(scope,actor,ct))return null;
        var review=await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==reviewId,ct);if(review==null)return null;
        var run=await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleOrDefaultAsync(r=>r.SelectionReviewId==reviewId,ct);
        if(run!=null)return new(run.Id,null);
        var window=review.RunWindowId==null?null:await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w=>w.Id==review.RunWindowId,ct);
        return window!=null&&await Scoped<VetPhotoRun>(scope).AnyAsync(r=>r.Id==window.RunId,ct)?new(window.RunId,window.Id):null;
    }
    public async Task<VetPhotoRecoverableRun?> FindAttemptRunAsync(VetDiaryScope scope,Guid attemptId,long actor,CancellationToken ct)
    {
        await CheckAsync(scope,ct);if(!await ImageActorAsync(scope,actor,ct))return null;
        var attempt=await Scoped<VetPhotoAttempt>(scope).AsNoTracking().SingleOrDefaultAsync(a=>a.Id==attemptId&&a.Kind=="image",ct);
        var window=attempt?.RunWindowId==null?null:await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w=>w.Id==attempt.RunWindowId,ct);
        var run=window==null?null:await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==window.RunId&&r.ActorUserId==attempt!.ActorUserId,ct);
        return run==null?null:new(scope,run.Id,run.ActorUserId);
    }
    public async Task<IReadOnlyList<Guid>?> ResolveCurrentReferencesAsync(VetDiaryScope scope,IReadOnlyList<Guid> sourceIds,long actor,CancellationToken ct)
    {
        await CheckAsync(scope,ct);if(!await ImageActorAsync(scope,actor,ct)||sourceIds.Count is <1 or >50
            ||sourceIds.Any(s=>s==Guid.Empty)||sourceIds.Distinct().Count()!=sourceIds.Count)return null;
        var result=new List<Guid>();
        foreach(var id in sourceIds)
        {
            var source=await ImageSourceAsync(scope,id,ct);if(source==null)return null;
            var original=await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking().SingleOrDefaultAsync(r=>r.InputRevisionId==source.CurrentInputRevisionId&&r.State=="retained",ct);
            if(original==null)return null;result.Add(original.Id);
        }
        return result;
    }
    public async Task<IReadOnlyList<VetPhotoRecoverableRun>> GetRecoverableRunsAsync(long familyId,long botDbId,int limit,CancellationToken ct)
    {
        _guard.Family(familyId);if(limit is <1 or >5)return [];
        await RepairObviousRunsAsync(familyId,botDbId,ct);
        var bot=await db.Bots.AsNoTracking().SingleOrDefaultAsync(b=>b.Id==botDbId&&b.FamilyId==familyId&&b.Role=="vet"&&b.Status==BotStatus.Active,ct);if(bot==null)return [];
        var result=new List<VetPhotoRecoverableRun>();var visited=new List<Guid>();
        for(var scanned=0;scanned<MaxRunRepairsPerPoll&&result.Count<limit;)
        {
            var query=db.Set<VetPhotoRun>().AsNoTracking().Where(r=>r.FamilyId==familyId&&r.BotDbId==botDbId&&r.TelegramBotId==bot.TelegramBotId
                &&!visited.Contains(r.Id)&&r.CancelledAt==null&&(r.State=="preview"||r.State=="approved"||r.State=="running")
                &&db.FamilyMembers.Any(m=>m.FamilyId==familyId&&m.TelegramUserId==r.ActorUserId&&m.Status==FamilyMemberStatus.Approved)
                &&(r.ChatId==r.ActorUserId&&r.TopicId==null||db.Places.Any(p=>p.BotId==botDbId&&p.ChatId==r.ChatId&&p.TopicId==r.TopicId&&p.Status==PlaceStatus.Approved))
                &&(db.Set<VetPhotoReview>().Any(v=>v.Id==r.SelectionReviewId&&v.FamilyId==familyId&&v.BotDbId==botDbId
                    &&v.TelegramBotId==bot.TelegramBotId&&v.ChatId==r.ChatId&&v.TopicId==r.TopicId
                    &&v.SelectionJson==r.SelectionJson&&db.Set<VetProfile>().Any(p=>p.Id==v.ProfileId&&p.FamilyId==familyId&&p.BotDbId==botDbId&&p.Revision==v.ProfileRevision))
                   ||db.Set<VetPhotoRunWindow>().Any(w=>w.RunId==r.Id&&w.Ordinal==r.NextWindowOrdinal&&w.State=="awaiting_review"
                    &&db.Set<VetPhotoReview>().Any(v=>v.Id==w.ComparisonReviewId&&v.RunWindowId==w.Id&&v.FamilyId==familyId&&v.BotDbId==botDbId
                        &&v.TelegramBotId==bot.TelegramBotId&&v.ChatId==r.ChatId&&v.TopicId==r.TopicId&&v.State=="accepted"&&v.Kind=="reextract_comparison"
                        &&v.CompletePreviewDelivered&&db.VetDiaryActions.Any(a=>a.Id==v.ActionId&&a.FamilyId==familyId&&a.BotDbId==botDbId
                            &&a.TelegramBotId==bot.TelegramBotId&&a.ChatId==r.ChatId&&a.TopicId==r.TopicId&&a.ActorUserId==v.DecisionActorUserId))))
                &&(r.State=="preview"&&db.Set<VetPhotoReview>().Any(v=>v.Id==r.SelectionReviewId&&v.State=="preview"&&!v.CompletePreviewDelivered)
                   ||r.State=="running"&&db.Set<VetPhotoRunWindow>().Any(w=>w.RunId==r.Id&&w.Ordinal==r.NextWindowOrdinal
                    &&(w.ComparisonReviewId!=null&&db.Set<VetPhotoReview>().Any(v=>v.Id==w.ComparisonReviewId
                        &&(v.State=="accepted"&&v.Kind=="reextract_comparison"&&db.VetDiaryActions.Any(a=>a.Id==v.ActionId
                            &&a.FamilyId==familyId&&a.BotDbId==botDbId&&a.TelegramBotId==bot.TelegramBotId&&a.ChatId==r.ChatId&&a.TopicId==r.TopicId&&a.ActorUserId==v.DecisionActorUserId)
                           ||v.State=="preview"&&!v.CompletePreviewDelivered))
                       ||w.ComparisonReviewId==null&&(w.State=="queued"||w.State=="running")
                        &&db.Set<VetPhotoAttempt>().Any(a=>a.RunWindowId==w.Id&&a.Kind=="image")
                        &&!db.Set<VetPhotoAttempt>().Any(a=>a.RunWindowId==w.Id&&a.Kind=="image"
                            &&!(a.State=="returned"&&!a.ReservedResultSlot&&a.ExtractionResultId!=null
                                ||a.State=="failed"&&!a.ReservedResultSlot||a.State=="unknown"&&a.ReservedResultSlot&&a.ExtractionResultId==null))))));
            var candidates=await query.OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id).Take(Math.Min(5,MaxRunRepairsPerPoll-scanned)).ToListAsync(ct);
            if(candidates.Count==0)break;
            foreach(var run in candidates)
            {
                visited.Add(run.Id);scanned++;var scope=new VetDiaryScope(familyId,botDbId,bot.TelegramBotId,run.ChatId,run.TopicId);
                if(await RepairScheduledRunAsync(scope,run.Id,run.ActorUserId,ct))continue;
                result.Add(new(scope,run.Id,run.ActorUserId));if(result.Count==limit)break;
            }
        }
        return result;
    }
    private async Task<bool> ReplacementSelectionCurrentAsync(VetDiaryScope scope,VetPhotoRun run,VetPhotoRunWindow window,
        VetPhotoRunInputSnapshot[] selected,VetPhotoReview next,CancellationToken ct)
    {
        var items=ReadRunSelection<VetPhotoDiarySelection>(next.SelectionJson,50,VetPhotoReviewBounds.MaxSelectionEntryChars);
        if(items==null||items.Length!=selected.Select(s=>s.SourceId).Distinct().Count()
            ||items.Select(i=>i.SourceId).Distinct().Count()!=items.Length||items.Select(i=>i.CandidateId).Distinct().Count()!=items.Length)return false;
        foreach(var item in items)
        {
            var snapshot=selected.SingleOrDefault(s=>s.SourceId==item.SourceId&&s.InputRevisionId==item.InputRevisionId);
            if(snapshot==null||item.ProfileId!=next.ProfileId||item.ProfileRevision!=next.ProfileRevision
                ||item.Disposition is not ("save" or "correct" or "keep")||item.ExpectedCurrentInputId!=snapshot.ExpectedCurrentInputId
                ||item.ExpectedSourceOrdinal!=snapshot.ExpectedSourceOrdinal||item.OriginalReferenceId!=snapshot.OriginalReferenceId
                ||item.OriginalReferenceRevision!=snapshot.ExpectedReferenceRevision||item.OriginalReferenceState!="retained")return false;
            var source=await ImageSourceAsync(scope,item.SourceId,ct);
            var candidate=await Scoped<VetPhotoCandidate>(scope).AsNoTracking().SingleOrDefaultAsync(c=>c.Id==item.CandidateId&&c.SourceId==item.SourceId&&c.CandidateOrdinal==0,ct);
            var batch=source?.BatchId==null?null:await BatchAsync(scope,source.BatchId.Value,ct);
            if(source==null||candidate==null||batch==null||batch.Id!=item.BatchId||batch.ReviewRevision!=item.BatchReviewRevision
                ||batch.ProfileId!=item.ProfileId||batch.ProfileRevision!=item.ProfileRevision||candidate.Revision!=item.CandidateRevision
                ||candidate.ExtractionResultId!=item.ExpectedCandidateExtractionId||candidate.EventId!=item.EventId||candidate.EventRevision!=item.EventRevision)return false;
            var attempt=await Scoped<VetPhotoAttempt>(scope).AsNoTracking().SingleOrDefaultAsync(a=>a.Id==snapshot.AttemptKey,ct);
            if(attempt==null||attempt.RunWindowId!=window.Id||attempt.ActorUserId!=run.ActorUserId||attempt.Kind!="image"
                ||attempt.SourceId!=item.SourceId||attempt.InputRevisionId!=item.InputRevisionId
                ||attempt.ExpectedCurrentInputId!=snapshot.ExpectedCurrentInputId||attempt.ExpectedSourceOrdinal!=snapshot.ExpectedSourceOrdinal
                ||attempt.ExtractionResultId!=item.ExtractionResultId)return false;
            if(item.ExtractionResultId==null)
            {if(item.Disposition!="keep"||!(attempt.State=="unknown"&&attempt.ReservedResultSlot||attempt.State=="failed"&&!attempt.ReservedResultSlot))return false;}
            else
            {
                var result=await Scoped<VetPhotoExtraction>(scope).AsNoTracking().SingleOrDefaultAsync(e=>e.Id==item.ExtractionResultId&&e.AttemptId==attempt.Id
                    &&e.SourceId==item.SourceId&&e.InputRevisionId==item.InputRevisionId,ct);
                if(result==null||attempt.ReservedResultSlot||!(attempt.State=="returned"&&result.State=="comparison"
                    ||item.Disposition=="keep"&&attempt.State=="failed"&&result.State=="invalid"))return false;
            }
        }
        return true;
    }
    public Task<VetPhotoRunChange> ReplaceComparisonAsync(VetPhotoRunHandle handle,Guid windowId,Guid priorId,int priorRevision,Guid newReviewId,CancellationToken ct)=>
        WorkflowAsync(handle.Scope,handle.ActorUserId,new VetPhotoRunChange(VetPhotoWorkflowStatus.Refused,null),async()=>
        {
            var proof=await ReadWindowProofLockedAsync(handle,windowId,ct);if(proof==null)return new(VetPhotoWorkflowStatus.Stale,null);
            var p=proof.Value;
            if(p.Run.State!="running"||p.Run.CancelledAt!=null||p.Window.State!="awaiting_review"||p.Window.Ordinal!=p.Run.NextWindowOrdinal
                ||p.Window.ComparisonReviewId!=priorId)return new(VetPhotoWorkflowStatus.Stale,p.Run);
            var old=await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==priorId,ct);
            var next=await Scoped<VetPhotoReview>(handle.Scope).AsNoTracking().SingleOrDefaultAsync(r=>r.Id==newReviewId,ct);
            if(old==null||old.Revision!=priorRevision||old.State is not ("preview" or "stale")||old.RunWindowId!=windowId
                ||next==null||next.Id==old.Id||next.State!="preview"||next.Kind!="reextract_comparison"||next.RunWindowId!=windowId
                ||!await ReviewCurrentAsync(handle.Scope,next,ct)
                ||!await ReplacementSelectionCurrentAsync(handle.Scope,p.Run,p.Window,p.Inputs,next,ct))return new(VetPhotoWorkflowStatus.Stale,p.Run);
            foreach(var s in p.Inputs)if(!await RunInputCurrentAsync(handle.Scope,s,ct))return new(VetPhotoWorkflowStatus.Stale,p.Run);
            db.Attach(old);old.State="stale";old.CompletePreviewDelivered=false;
            db.Attach(p.Window);p.Window.ComparisonReviewId=next.Id;
            return new(VetPhotoWorkflowStatus.Applied,p.Run,next,p.Window);
        },ct);
}
