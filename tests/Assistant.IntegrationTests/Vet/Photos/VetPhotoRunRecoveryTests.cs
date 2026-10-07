using System.Globalization;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoRunRecoveryTests:VetTestBase
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private VetPhotoStore Store(VetTestSession s)=>new(s.Context,s.Current,Clock,new(),new VetPhotoImageDecoder());
    private VetPhotoRunApplication App(VetTestSession s)
    {
        var p=Store(s);var composer=new VetPhotoReviewComposer(p,p,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);
        return new(p,p,p,p,p,s.Profiles,s.Diary,composer,new ReadApprovals(s),NullLogger<VetPhotoRunApplication>.Instance);
    }
    private sealed record Window(VetPhotoRun Run,VetPhotoRunWindow Value,IReadOnlyList<VetPhotoAdmission> Sources);
    private sealed record Dispatch(VetPhotoImageClaim Claim,VetPhotoExtraction? Result);
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static byte[] Png(int id)
    {using var b=new SKBitmap(new SKImageInfo(32,24,SKColorType.Rgba8888,SKAlphaType.Premul));b.Erase(new SKColor((byte)id,70,180));using var i=SKImage.FromBitmap(b);using var data=i.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();}
    private async Task<VetPhotoAdmission> Original(VetTestSession s,int id,bool edit=false,bool protect=false)
    {
        var update=await s.Context.Bots.Where(b=>b.Id==Bot.BotDbId).Select(b=>b.LastUpdateId).SingleAsync(Ct)+1;
        var m=Text(edit?$"synthetic edited caption {update}":"synthetic caption",id) with{Kind=Assistant.Domain.Messages.MessageKind.Photo,IsEdit=edit,EditedAt=edit?Now.AddSeconds(update):null};
        var bytes=Png(id);var admitted=await Store(s).AdmitAsync(Scope,m,update,new($"synthetic-file-{id}",$"synthetic-unique-{id}","synthetic.png","image/png",bytes.Length,32,24),null,Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);var dbMessage=await s.Messages.StoreAsync(Bot.TelegramBotId,update,m,Ct);
        (await Store(s).BindMessageAsync(Scope,admitted.Source!.Id,dbMessage.MessageDbId.ShouldNotBeNull(),Ct)).ShouldBeTrue();
        var reserve=await Store(s).ReserveDownloadAsync(Scope,admitted.Source.Id,admitted.Input!.Id,111,Ct);
        if(reserve.Status==VetPhotoArchiveStatus.Reserved)
        {var claim=reserve.Claim.ShouldNotBeNull();var decoded=new VetPhotoImageDecoder().Decode(bytes,Ct).Image.ShouldNotBeNull();
         (await Store(s).CommitOriginalAsync(new(Scope,111,claim.Attempt.Id,claim.ClaimToken,admitted.Input.Id,bytes,decoded),Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);}
        else reserve.Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        if(protect)
        {var batch=(await Store(s).GetBatchAsync(Scope,admitted.Source.BatchId!.Value,111,Ct)).ShouldNotBeNull();
         (await Store(s).CancelRemainderAsync(Scope,batch.Batch.Id,batch.Batch.ReviewRevision,111,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);}
        return admitted;
    }
    private async Task Delivered(VetTestSession s,VetPhotoReview r)
    {
        var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!;pages.ShouldNotBeEmpty();
        var h=new VetPhotoReviewHandle(Scope,r.Id,r.Revision,r.OperationKey,111);
        for(var i=0;i<pages.Length;i++)(await Store(s).RecordPageDeliveryAsync(h,i,7000+i,Hash(pages[i]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).CompleteDeliveryAsync(h,6999+pages.Length,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
    }
    private async Task<Window> Start(VetTestSession s,IReadOnlyList<VetPhotoAdmission> sources,VetPhotoRunSelectionMode mode=VetPhotoRunSelectionMode.Selected,bool continueNow=true,bool approve=true)
    {
        var profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        var sourceInputs=sources.Select(x=>x.Input!.Id).ToArray();
        var references=await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().Where(x=>sourceInputs.Contains(x.InputRevisionId)&&x.State=="retained").Select(x=>x.Id).ToListAsync(Ct);
        var staged=await Store(s).StageRunAsync(new(Scope,Guid.NewGuid(),111,profile.Revision,VetPhotoRunPurpose.Reprocess,mode,"gpt-6.1-sol","codex-cli",ReferenceIds:references),Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);await Delivered(s,staged.Review.ShouldNotBeNull());
        await s.Context.Set<VetPhotoRun>().Where(x=>x.Id==staged.Run!.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.CreatedAt,Now.AddSeconds(sources[0].Source!.TelegramMessageId)),Ct);
        if(!approve)return new(staged.Run!,new VetPhotoRunWindow(),sources);
        var h=new VetPhotoRunHandle(Scope,staged.Run!.Id,111);var review=(await Store(s).ReadPreviewAsync(Scope,staged.Review!.Id,111,Ct)).ShouldNotBeNull();
        (await Store(s).ApproveRunAsync(h,new(Scope,review.Id,review.Revision,review.OperationKey,111,review.AcceptancePromptMessageId),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if(!continueNow)return new((await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(x=>x.Id==h.RunId,Ct)),
            (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(x=>x.RunId==h.RunId,Ct)),sources);
        var continued=await Store(s).ContinueRunAsync(h,Ct);continued.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        return new(continued.Run.ShouldNotBeNull(),continued.Window.ShouldNotBeNull(),sources);
    }
    private static string Result(Guid source,Guid input,int ordinal,bool missingClock=false)=>JsonSerializer.Serialize(new
    {schema_version=1,photo_source_id=source,input_revision_id=input,kind="meter",displays=new[]{new{value_text="6.1250",decimal_value=6.1250m,unit="mmol/L",year=2031,
        year_displayed=true,month=5,day=11,time=missingClock?null:$"10:{ordinal%60:00}:05",offset="+00:00"}},reasons=Array.Empty<string>(),notes=(string?)null},Json);
    private async Task<Dispatch> Finish(VetTestSession s,Window w,int selectedIndex,string outcome="returned",bool missingClock=false)
    {
        var snapshots=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!;var snap=snapshots[selectedIndex];
        var claim=(await Store(s).ClaimScheduledImageAsync(Scope,snap.AttemptKey,111,Ct)).Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope,claim.AttemptKey,claim.ClaimToken,111,Ct)).ShouldBeTrue();
        if(outcome is "unknown" or "failed")
        {(await Store(s).RecordImageFailureAsync(Scope,claim.AttemptKey,claim.ClaimToken,111,outcome=="unknown"?"outcome_unknown":"provider_refused",
            outcome=="unknown"?VetPhotoImageFailureDisposition.OutcomeUnknown:VetPhotoImageFailureDisposition.KnownNotDispatched,Ct)).ShouldBeTrue();return new(claim,null);}
        var json=outcome=="invalid"?"{}":Result(claim.SourceId,claim.InputRevisionId,selectedIndex,missingClock);
        var complete=await Store(s).CompleteImageAsync(new(Scope,claim.AttemptKey,claim.ClaimToken,111,claim.SourceId,claim.InputRevisionId,"gpt-6.1-sol",json),Ct);
        complete.Status.ShouldBe(outcome=="invalid"?VetPhotoImageStatus.InvalidResult:VetPhotoImageStatus.ProposedDelta);
        return new(claim,complete.Extraction.ShouldNotBeNull());
    }
    private async Task<VetPhotoReview> Show(VetTestSession s,Window w)
    {
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic show",900),w.Run.Id,Ct);
        var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(x=>x.Id==w.Value.Id,Ct);
        var review=(await Store(s).ReadPreviewAsync(Scope,window.ComparisonReviewId.ShouldNotBeNull(),111,Ct)).ShouldNotBeNull();
        review.CompletePreviewDelivered.ShouldBeTrue();review.AcceptancePromptMessageId.ShouldNotBeNull();
        return review;
    }
    private async Task<VetMutationResult> Accept(VetTestSession s,VetPhotoReview review,long actor=222)=>
        await s.Diary.ApplyPhotoReviewAsync(new(Scope,review.Id,review.Revision,review.OperationKey,actor,review.AcceptancePromptMessageId),Ct);
    private static Task<string> Candidates(VetTestSession s)=>Snapshot(s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x=>x.Id));
    private static async Task<string> Snapshot<T>(IQueryable<T> query)=>JsonSerializer.Serialize(await query.ToListAsync(Ct),Json);


    private async Task<string> Conserved(VetTestSession s)=>JsonSerializer.Serialize(new
    {
        Attempts=await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(x=>x.Id)),
        Results=await Snapshot(s.Context.Set<VetPhotoExtraction>().AsNoTracking().OrderBy(x=>x.Id)),
        Sources=await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking().OrderBy(x=>x.Id)),
        Inputs=await Snapshot(s.Context.Set<VetPhotoInputRevision>().AsNoTracking().OrderBy(x=>x.Id)),
        Candidates=await Candidates(s),
        References=await Snapshot(s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x=>x.Id)),
        Blobs=await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x=>x.Id)),
        Facts=await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().OrderBy(x=>x.Id)),
        Actions=await Snapshot(s.Context.VetDiaryActions.AsNoTracking().OrderBy(x=>x.Id)),
        Windows=await Snapshot(s.Context.Set<VetPhotoRunWindow>().AsNoTracking().OrderBy(x=>x.Id))
    },Json);
    private async Task<string> Manifest(VetTestSession s,Guid run)=>JsonSerializer.Serialize(await s.Context.Set<VetPhotoRun>().AsNoTracking()
        .Where(x=>x.Id==run).Select(x=>new{x.Id,x.OperationKey,x.ActorUserId,x.SelectionReviewId,x.SelectionMode,x.SelectionJson,x.ModelName,x.SelectedCount,x.NextWindowOrdinal,x.CancelledAt,x.CreatedAt}).SingleAsync(Ct),Json);
    private async Task Corrupt(VetTestSession s,Window w,string kind)
    {
        switch(kind)
        {
            case "profile":await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.ProfileRevision,r=>r.ProfileRevision-1),Ct);break;
            case "hash":await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Fingerprint,new string('0',64)),Ct);break;
            case "window":await s.Context.Set<VetPhotoRunWindow>().Where(r=>r.Id==w.Value.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.SelectionJson,"[]"),Ct);break;
            case "actor":await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.DecisionActorUserId,(long?)222),Ct);break;
            case "delivery":await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.CompletePreviewDelivered,false),Ct);break;
            case "reference":await s.Context.Set<VetPhotoOriginalReference>().Where(r=>r.InputRevisionId==w.Sources[0].Input!.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Revision,r=>r.Revision+1),Ct);break;
            case "pointer":await s.Context.Set<VetPhotoSource>().Where(r=>r.Id==w.Sources[0].Source!.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.CurrentOrdinal,r=>r.CurrentOrdinal+1),Ct);break;
            default:throw new InvalidOperationException();
        }
    }
    [Theory]
    [InlineData("profile")][InlineData("hash")][InlineData("window")][InlineData("actor")]
    [InlineData("delivery")][InlineData("reference")][InlineData("pointer")]
    public async Task Six_invalid_oldest_scheduled_runs_hold_durably_and_do_not_hide_a_later_valid_call(string defect)
    {
        await SeedAsync();await using var s=Open();var bad=new List<Window>();
        for(var i=1;i<=6;i++)bad.Add(await Start(s,[await Original(s,i,protect:true)]));
        var valid=await Start(s,[await Original(s,7,protect:true)]);
        for(var i=0;i<bad.Count;i++)await Corrupt(s,bad[i],defect);
        var before=await Conserved(s);var manifests=new Dictionary<Guid,string>();foreach(var w in bad)manifests[w.Run.Id]=await Manifest(s,w.Run.Id);
        var due=await Store(s).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct);
        due.Count.ShouldBe(1);due.Single().SourceId.ShouldBe(valid.Sources.Single().Source!.Id);
        due.Single().ScheduledAttemptKey.ShouldBe(JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(valid.Value.SelectionJson,Json)!.Single().AttemptKey);
        foreach(var w in bad)
        {
            (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(x=>x.Id==w.Run.Id,Ct)).State.ShouldBe("stale");
            (await Manifest(s,w.Run.Id)).ShouldBe(manifests[w.Run.Id]);
        }
        (await Conserved(s)).ShouldBe(before);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        await using var restart=Open();var again=await Store(restart).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct);
        again.Select(x=>x.ScheduledAttemptKey).ShouldBe(due.Select(x=>x.ScheduledAttemptKey));
        (await Conserved(restart)).ShouldBe(before);
    }
    [Theory]
    [InlineData("profile")][InlineData("hash")][InlineData("window")][InlineData("actor")]
    [InlineData("reference")][InlineData("pointer")]
    public async Task Six_invalid_terminal_runs_do_not_starve_later_comparison_recovery(string defect)
    {
        await SeedAsync();await using var s=Open();var bad=new List<Window>();
        for(var i=1;i<=6;i++){var w=await Start(s,[await Original(s,i,protect:true)]);await Finish(s,w,0);bad.Add(w);}
        var valid=await Start(s,[await Original(s,7,protect:true)]);await Finish(s,valid,0);
        foreach(var w in bad)await Corrupt(s,w,defect);var before=await Conserved(s);
        var recoverable=await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct);
        recoverable.Select(x=>x.RunId).ShouldBe([valid.Run.Id]);(await Conserved(s)).ShouldBe(before);
        await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        var linked=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(x=>x.Id==valid.Value.Id,Ct);
        var review=(await Store(s).ReadPreviewAsync(Scope,linked.ComparisonReviewId.ShouldNotBeNull(),111,Ct)).ShouldNotBeNull();
        review.CompletePreviewDelivered.ShouldBeTrue();review.Kind.ShouldBe("reextract_comparison");
        foreach(var w in bad)(await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(x=>x.Id==w.Value.Id,Ct)).ComparisonReviewId.ShouldBeNull();
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("delivered_preview")][InlineData("approved")][InlineData("queued")][InlineData("claimed")][InlineData("dispatched")][InlineData("delivered_comparison")]
    public async Task Six_idle_oldest_runs_are_filtered_before_recovery_limit_without_implicit_continue(string idle)
    {
        await SeedAsync();await using var s=Open();var ids=new List<Guid>();
        for(var i=1;i<=6;i++)
        {
            var original=await Original(s,i,protect:true);var w=await Start(s,[original],continueNow:idle is not ("delivered_preview" or "approved"),approve:idle!="delivered_preview");ids.Add(w.Run.Id);
            if(idle=="delivered_comparison"){await Finish(s,w,0);await Show(s,w);}
            else if(idle is "claimed" or "dispatched")
            {
                var key=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!.Single().AttemptKey;
                var claim=(await Store(s).ClaimScheduledImageAsync(Scope,key,111,Ct)).Claim.ShouldNotBeNull();
                if(idle=="dispatched")(await Store(s).MarkImageDispatchedAsync(Scope,key,claim.ClaimToken,111,Ct)).ShouldBeTrue();
            }
        }
        var valid=await Start(s,[await Original(s,7,protect:true)]);await Finish(s,valid,0);
        var before=await Conserved(s);var count=await s.Context.Set<VetPhotoAttempt>().CountAsync(Ct);
        (await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct)).Select(x=>x.RunId).ShouldBe([valid.Run.Id]);
        (await Conserved(s)).ShouldBe(before);(await s.Context.Set<VetPhotoAttempt>().CountAsync(Ct)).ShouldBe(count);
        foreach(var id in ids)(await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(x=>x.Id==id,Ct)).State.ShouldNotBe("stale");
    }
    [Fact]
    public async Task Accepted_action_marker_reconciles_after_profile_change_without_stranding_already_written_fact()
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);await Finish(s,w,0);var review=await Show(s,w);
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied);
        var facts=await Snapshot(s.Context.Set<VetEvent>().AsNoTracking());var actions=await Snapshot(s.Context.VetDiaryActions.AsNoTracking());
        await s.Context.Set<VetProfile>().Where(p=>p.FamilyId==FamilyId&&p.BotDbId==Bot.BotDbId).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.Revision,p=>p.Revision+1),Ct);
        (await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct)).Select(x=>x.RunId).ShouldBe([w.Run.Id]);
        await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        var current=await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct);current.State.ShouldBe("completed");current.NextWindowOrdinal.ShouldBe(1);
        var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);window.State.ShouldBe("completed");window.ActionId.ShouldBe(review.ActionId??(await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(Ct)).Id);
        (await Snapshot(s.Context.Set<VetEvent>().AsNoTracking())).ShouldBe(facts);(await Snapshot(s.Context.VetDiaryActions.AsNoTracking())).ShouldBe(actions);
        (await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct)).ShouldBeEmpty();
    }
    [Fact]
    public async Task Profile_hold_preserves_unknown_charge_and_live_dispatch_with_visible_restart_status_and_explicit_cancel()
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1,protect:true),await Original(s,2,protect:true)]);
        await Finish(s,w,0,"unknown");var key=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)![1].AttemptKey;
        var live=(await Store(s).ClaimScheduledImageAsync(Scope,key,111,Ct)).Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope,key,live.ClaimToken,111,Ct)).ShouldBeTrue();
        await s.Context.Set<VetProfile>().Where(p=>p.FamilyId==FamilyId&&p.BotDbId==Bot.BotDbId).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.Revision,p=>p.Revision+1),Ct);
        var before=await Conserved(s);var manifest=await Manifest(s,w.Run.Id);var capacity=await Store(s).GetCapacityAsync(Scope,111,Ct);capacity.ReservedResults.ShouldBe(2L);
        (await Store(s).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct)).ShouldBeEmpty();
        (await Store(s).GetRunAsync(new(Scope,w.Run.Id,222),0,5,Ct)).ShouldNotBeNull().Run.State.ShouldBe("stale");
        (await Conserved(s)).ShouldBe(before);(await Manifest(s,w.Run.Id)).ShouldBe(manifest);
        (await Store(s).ContinueRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        await using var restart=Open();await App(restart).ShowAsync(Bot,restart.Telegram,Text("synthetic show"),w.Run.Id,Ct);
        string.Join("\n",restart.Telegram.SentMessages.Select(m=>m.Text)).ShouldContain("\u0443\u0434\u0435\u0440\u0436\u0430\u043d");
        (await Conserved(restart)).ShouldBe(before);(await Store(restart).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(2L);
        (await Store(restart).CancelRunAsync(new(Scope,w.Run.Id,222),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await restart.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a=>a.Id==key,Ct)).State.ShouldBe("dispatched");
        (await Store(restart).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(2L);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Explicit_stale_returned_result_requires_current_source_and_fresh_complete_confirmation(bool changedSource)
    {
        await SeedAsync();await using var s=Open();var one=await Original(s,1);var two=await Original(s,2);var w=await Start(s,[one,two]);
        var manifest=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!;
        var oneOrdinal=Array.FindIndex(manifest,x=>x.InputRevisionId==one.Input!.Id);oneOrdinal.ShouldBeInRange(0,manifest.Length-1);
        var result=await Finish(s,w,oneOrdinal);
        await s.Context.Set<VetProfile>().Where(p=>p.FamilyId==FamilyId&&p.BotDbId==Bot.BotDbId).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.Revision,p=>p.Revision+1),Ct);
        s.Context.ChangeTracker.Clear();
        if(changedSource)await Original(s,1,edit:true);
        var before=await Conserved(s);await Store(s).GetRunAsync(new(Scope,w.Run.Id,111),0,5,Ct);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("stale");
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic show"),w.Run.Id,Ct);(await Conserved(s)).ShouldBe(before);
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic exact result"),w.Run.Id,w.Value.Id,result.Claim.SourceId,result.Claim.InputRevisionId,result.Result!.Id,false,Ct);
        if(changedSource)
        {(await s.Context.Set<VetPhotoReview>().CountAsync(r=>r.Kind=="correction",Ct)).ShouldBe(0);(await Conserved(s)).ShouldBe(before);return;}
        var proposals=await s.Context.Set<VetPhotoReview>().AsNoTracking().Where(r=>r.Kind=="correction").ToListAsync(Ct);
        proposals.Count.ShouldBe(1,string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text)));
        var fresh=proposals.Single();
        fresh.CompletePreviewDelivered.ShouldBeTrue();fresh.RunWindowId.ShouldBe(w.Value.Id);
        var selection=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(fresh.SelectionJson,Json)!.Single();selection.SourceId.ShouldBe(one.Source!.Id);selection.ExtractionResultId.ShouldBe(result.Result.Id);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        (await Accept(s,fresh)).Status.ShouldBe(VetMutationStatus.Applied);
        var bound=await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x=>x.Id==one.Source!.Id,Ct);
        var fact=await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct);fact.SourceMessageDbId.ShouldBe(bound.SourceMessageDbId.ShouldNotBeNull());fact.InputRevisionId.ShouldBe(one.Input!.Id);fact.ExtractionResultId.ShouldBe(result.Result.Id);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("stale");
        (await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a=>a.SourceId==two.Source!.Id&&a.Kind=="image",Ct)).State.ShouldBe("queued");
    }
    [Theory]
    [InlineData("member")][InlineData("place")][InlineData("topic")][InlineData("bot")]
    public async Task Hold_status_and_cancellation_recheck_current_actor_and_exact_place(string gate)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);await Corrupt(s,w,"profile");
        await Store(s).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct);var before=await Conserved(s);var scope=Scope;
        if(gate=="member")await s.Context.FamilyMembers.Where(m=>m.FamilyId==FamilyId&&m.TelegramUserId==222).ExecuteUpdateAsync(u=>u.SetProperty(m=>m.Status,FamilyMemberStatus.Denied),Ct);
        if(gate=="place")await s.Context.Places.Where(p=>p.BotId==Bot.BotDbId&&p.ChatId==Scope.ChatId&&p.TopicId==Scope.TopicId).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.Status,PlaceStatus.Denied),Ct);
        if(gate=="topic")scope=Scope with{TopicId=999};
        if(gate=="bot")await s.Context.Bots.Where(b=>b.Id==Bot.BotDbId).ExecuteUpdateAsync(u=>u.SetProperty(b=>b.Status,Assistant.Domain.Bots.BotStatus.Disabled),Ct);
        (await Store(s).GetRunAsync(new(scope,w.Run.Id,222),0,5,Ct)).ShouldBeNull();
        var cancelled=await Store(s).CancelRunAsync(new(scope,w.Run.Id,222),Ct);
        cancelled.Status.ShouldBe(gate=="bot"?VetPhotoWorkflowStatus.NotFound:VetPhotoWorkflowStatus.Refused);
        (await Conserved(s)).ShouldBe(before);(await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("stale");
    }
    [Fact]
    public async Task Failed_stale_state_write_rolls_back_and_same_context_retry_then_restart_preserves_manifest_and_charge()
    {
        await SeedAsync();Guid id;string manifest;string conserved;
        await using(var seed=Open())
        {
            var w=await Start(seed,[await Original(seed,1,protect:true)]);await Finish(seed,w,0,"unknown");await Corrupt(seed,w,"profile");id=w.Run.Id;
            manifest=await Manifest(seed,id);conserved=await Conserved(seed);
        }
        var failure=new FailRunHold();await using var s=Open(interceptor:failure);
        var error=await Should.ThrowAsync<DbUpdateException>(()=>Store(s).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct));failure.Fired.ShouldBeTrue();
        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("Synthetic run hold failure.");
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(x=>x.Id==id,Ct)).State.ShouldBe("running");
        (await Manifest(s,id)).ShouldBe(manifest);(await Conserved(s)).ShouldBe(conserved);
        (await Store(s).GetDueAsync(FamilyId,Bot.BotDbId,5,Ct)).ShouldBeEmpty();
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(x=>x.Id==id,Ct)).State.ShouldBe("stale");
        await using var restart=Open();(await Store(restart).GetRunAsync(new(Scope,id,222),0,5,Ct)).ShouldNotBeNull().Run.State.ShouldBe("stale");
        (await Manifest(restart,id)).ShouldBe(manifest);(await Conserved(restart)).ShouldBe(conserved);
        (await Store(restart).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(1L);
    }
    private sealed class FailRunHold:DbCommandInterceptor
    {
        public bool Fired{get;private set;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(!Fired&&command.CommandText.Replace("\"","",StringComparison.Ordinal).Contains("UPDATE vet_photo_runs",StringComparison.OrdinalIgnoreCase))
            {Fired=true;throw new InvalidOperationException("Synthetic run hold failure.");}
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ReadApprovals(VetTestSession s):IApprovalService
    {
        public Task<FamilyMemberStatus?> FindFamilyMemberStatusAsync(long family,long actor,CancellationToken ct)=>s.Context.FamilyMembers.Where(m=>m.FamilyId==family&&m.TelegramUserId==actor).Select(m=>(FamilyMemberStatus?)m.Status).SingleOrDefaultAsync(ct);
        public Task<PlaceStatus?> FindPlaceStatusAsync(long bot,long chat,int? topic,CancellationToken ct)=>s.Context.Places.Where(p=>p.BotId==bot&&p.ChatId==chat&&p.TopicId==topic).Select(p=>(PlaceStatus?)p.Status).SingleOrDefaultAsync(ct);
        public Task<long> GetOrCreatePendingPlaceAsync(long bot,long chat,int? topic,string title,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ApprovalResolution> ResolvePlaceApprovalAsync(long id,bool approve,CancellationToken ct)=>throw new NotSupportedException();
        public Task<PlaceStatus> GetPlaceStatusAsync(long id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<bool> GetPlaceReplyToAllAsync(long id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<long> GetOrCreatePendingFamilyMemberAsync(long family,long actor,string display,string? username,string bot,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ApprovalResolution> ResolveUserApprovalAsync(long id,bool approve,CancellationToken ct)=>throw new NotSupportedException();
        public Task<FamilyMemberStatus> GetFamilyMemberStatusAsync(long id,CancellationToken ct)=>throw new NotSupportedException();
    }
}
