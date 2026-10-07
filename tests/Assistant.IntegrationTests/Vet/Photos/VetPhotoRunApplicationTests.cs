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
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoRunApplicationTests:VetTestBase
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
    private async Task<VetPhotoAdmission> Original(VetTestSession s,int id,bool edit=false,bool protect=false,string? caption=null)
    {
        var update=await s.Context.Bots.Where(b=>b.Id==Bot.BotDbId).Select(b=>b.LastUpdateId).SingleAsync(Ct)+1;
        var m=Text(caption ?? (edit?$"synthetic edited caption {update}":"synthetic caption"),id) with{Kind=Assistant.Domain.Messages.MessageKind.Photo,IsEdit=edit,EditedAt=edit?Now.AddSeconds(update):null};
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
    private async Task<Window> Start(VetTestSession s,IReadOnlyList<VetPhotoAdmission> sources,VetPhotoRunSelectionMode mode=VetPhotoRunSelectionMode.Current)
    {
        var profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        var staged=await Store(s).StageRunAsync(new(Scope,Guid.NewGuid(),111,profile.Revision,VetPhotoRunPurpose.Reprocess,mode,"gpt-6.1-sol","codex-cli"),Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);await Delivered(s,staged.Review.ShouldNotBeNull());
        var h=new VetPhotoRunHandle(Scope,staged.Run!.Id,111);var review=(await Store(s).ReadPreviewAsync(Scope,staged.Review!.Id,111,Ct)).ShouldNotBeNull();
        (await Store(s).ApproveRunAsync(h,new(Scope,review.Id,review.Revision,review.OperationKey,111,review.AcceptancePromptMessageId),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
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

    private async Task<VetPhotoReview> ReviewFixStageInitial(VetTestSession s)
    {
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        return (await Store(s).StageRunAsync(new(Scope, Guid.NewGuid(), 111, profile.Revision,
            VetPhotoRunPurpose.Reprocess, VetPhotoRunSelectionMode.Current, "gpt-6.1-sol", "codex-cli"), Ct)).Review.ShouldNotBeNull();
    }
    private async Task<VetEvent> ReviewFixSaveBaseline(VetTestSession s, VetPhotoAdmission source, bool manual)
    {
        var claim = (await Store(s).ClaimCurrentImageAsync(Scope, source.Source!.Id, source.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var json = JsonSerializer.Serialize(new { schema_version = 1, photo_source_id = claim.SourceId, input_revision_id = claim.InputRevisionId,
            kind = "meter", displays = new[] { new { value_text = "5.7500", decimal_value = 5.7500m, unit = "mmol/L", year = 2031,
                year_displayed = true, month = 5, day = 11, time = "09:31:05", offset = "+00:00" } }, reasons = Array.Empty<string>(), notes = (string?)null }, Json);
        (await Store(s).CompleteImageAsync(new(Scope, claim.AttemptKey, claim.ClaimToken, 111, claim.SourceId, claim.InputRevisionId, "gpt-6.1-sol", json), Ct)).Status.ShouldBe(VetPhotoImageStatus.Installed);
        var p = Store(s); var composer = new VetPhotoReviewComposer(p,p,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);
        var review = (await composer.BuildBatchAsync(Scope, source.Source.BatchId!.Value, 111, null, false, Ct)).Review.ShouldNotBeNull();
        await Delivered(s, review); review = (await p.ReadPreviewAsync(Scope,review.Id,111,Ct)).ShouldNotBeNull();
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied);
        if (manual)
        {
            var b = (await p.GetBatchAsync(Scope,source.Source.BatchId.Value,111,Ct)).ShouldNotBeNull(); var item=b.Items.Single();
            (await p.ProposeHumanCorrectionAsync(new(Scope,b.Batch.Id,b.Batch.ReviewRevision,item.Candidate.Id,item.Candidate.Revision,item.Input.Id,
                item.Source.CurrentOrdinal,111,new("7.25","mmol/L",2031,5,11,"09:32:05","+00:00",CorrectionApproved:true)),Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
            review=(await composer.BuildBatchAsync(Scope,b.Batch.Id,111,[item.Candidate.Id],false,Ct)).Review.ShouldNotBeNull();
            await Delivered(s,review); review=(await p.ReadPreviewAsync(Scope,review.Id,111,Ct)).ShouldNotBeNull();
            (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied);
        }
        return await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
    }
    private static string ReviewFixPages(VetPhotoReview r) => string.Join("\n", JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!);
    private void ReviewFixAssertAllPagesDelivered(VetPhotoReview review, FakeTelegramClient client)
    {
        var pages=JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson,Json)!;
        pages.Length.ShouldBe(review.PageCount); pages.ShouldAllBe(page => page.Length<=3500);
        var delivered=JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson,Json)!;
        delivered.Length.ShouldBe(pages.Length);
        delivered.Select(p=>p.PageIndex).OrderBy(i=>i).ShouldBe(Enumerable.Range(0,pages.Length));
        foreach(var proof in delivered)
        {
            proof.TextHash.ShouldBe(Hash(pages[proof.PageIndex])); proof.MessageId.ShouldBeGreaterThan(0);
            client.SentMessages.Any(m=>m.ChatId==Scope.ChatId&&m.TopicId==Scope.TopicId&&m.Text==pages[proof.PageIndex]).ShouldBeTrue();
        }
        review.CompletePreviewDelivered.ShouldBeTrue();
        review.AcceptancePromptMessageId.ShouldBe(delivered.Single(p=>p.PageIndex==pages.Length-1).MessageId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewFix_comparison_shows_saved_or_manual_baseline_old_raw_effective_final_state_and_exact_correction(bool manual)
    {
        await SeedAsync(); await using var s=Open(); var source=await Original(s,1); var old=await ReviewFixSaveBaseline(s,source,manual);
        var w=await Start(s,[source]); var finished=await Finish(s,w,0); var review=await Show(s,w); var pages=ReviewFixPages(review);
        pages.ShouldContain($"Факт #{old.Id}, версия {old.Revision}: {(manual ? "7.25" : "5.75")} mmol/L");
        pages.ShouldContain(manual ? "2031-05-11 09:32:05" : "2031-05-11 09:31:05");
        pages.ShouldContain(manual ? "основание времени human_correction" : "основание времени image_or_caption");
        pages.ShouldContain(manual ? "основание значения/единицы human_correction" : "основание значения/единицы image/image");
        pages.ShouldContain("Прежний дисплей кандидата (данные)"); pages.ShouldContain(old.ExtractionResultId.ToString("D"));
        pages.ShouldContain("Прежний дисплей кандидата (данные): видно 5.7500");
        pages.ShouldContain("Выбранный новый дисплей (данные): видно 6.1250");
        pages.ShouldContain("Эффективное предложение: 6.125 mmol/L"); pages.ShouldContain("локальное время 2031-05-11 10:00:05");
        pages.ShouldContain("UTC 2031-05-11T10:00:05.0000000+00:00"); pages.ShouldContain("значение=image; единица=image; время=image_or_caption; профильные допущения=нет");
        var selected=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.Single();
        selected.Disposition.ShouldBe(manual ? "keep" : "correct"); selected.State!.Value.ShouldBe(manual ? 7.25m : 6.1250m);
        ReviewFixAssertAllPagesDelivered(review,s.Telegram);
        if(manual)
        {
            await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic explicit retained result",901),w.Run.Id,w.Value.Id,
                finished.Claim.SourceId,finished.Claim.InputRevisionId,finished.Result!.Id,false,Ct);
            review=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Kind=="reextract_comparison"&&r.State=="preview",Ct);
            ReviewFixAssertAllPagesDelivered(review,s.Telegram); ReviewFixPages(review).ShouldContain("После подтверждения: 6.125 mmol/L");
        }
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied);
        var saved=await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); saved.Id.ShouldBe(old.Id); saved.Value.ShouldBe(6.1250m);
        saved.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T10:00:05Z")); saved.ExtractionResultId.ShouldBe(finished.Result!.Id);
        saved.Revision.ShouldBe(old.Revision+1); s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task ReviewFix_missing_year_unit_and_zone_show_confirmed_year_and_profile_defaults_in_same_effective_review()
    {
        await SeedAsync(); await using var s=Open(); var source=await Original(s,1); var profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        (await s.Profiles.UpdateAsync(FamilyId,Bot.BotDbId,111,profile.Revision,[new("TimeZone","Europe/Berlin")],Ct)).Applied.ShouldBeTrue();
        var b=(await Store(s).GetBatchAsync(Scope,source.Source!.BatchId!.Value,111,Ct)).ShouldNotBeNull();
        profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        (await Store(s).RefreshProfileSnapshotAsync(Scope,b.Batch.Id,b.Batch.ReviewRevision,profile.Revision,111,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        b=(await Store(s).GetBatchAsync(Scope,b.Batch.Id,111,Ct)).ShouldNotBeNull();
        (await Store(s).ChangeAssumptionAsync(new(Scope,b.Batch.Id,b.Batch.ReviewRevision,b.Batch.ProfileRevision,111,VetPhotoAssumptionKind.Year,"2031"),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var w=await Start(s,[source]); var snap=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!.Single();
        var claim=(await Store(s).ClaimScheduledImageAsync(Scope,snap.AttemptKey,111,Ct)).Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope,claim.AttemptKey,claim.ClaimToken,111,Ct)).ShouldBeTrue();
        var json=JsonSerializer.Serialize(new {schema_version=1,photo_source_id=claim.SourceId,input_revision_id=claim.InputRevisionId,kind="meter",
            displays=new[]{new{value_text="6.1250",decimal_value=6.1250m,unit=(string?)null,year=(int?)null,year_displayed=false,month=5,day=11,time="10:00:05",offset=(string?)null}},reasons=Array.Empty<string>(),notes=(string?)null},Json);
        (await Store(s).CompleteImageAsync(new(Scope,claim.AttemptKey,claim.ClaimToken,111,claim.SourceId,claim.InputRevisionId,"gpt-6.1-sol",json),Ct)).Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        var review=await Show(s,w); var pages=ReviewFixPages(review); ReviewFixAssertAllPagesDelivered(review,s.Telegram);
        pages.ShouldContain("Выбранный новый дисплей (данные): видно 6.1250");
        pages.ShouldContain("год , показан False"); pages.ShouldContain("Допущения партии: год 2031; подтверждён True");
        pages.ShouldContain("профильная единица mmol/L"); pages.ShouldContain("профильная зона Europe/Berlin");
        pages.ShouldContain("Эффективное предложение: 6.125 mmol/L; локальное время 2031-05-11 10:00:05; зона Europe/Berlin");
        pages.ShouldContain("UTC 2031-05-11T08:00:05.0000000+00:00"); pages.ShouldContain("единица=batch_default; время=batch_year; профильные допущения=да");
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied); var fact=await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        fact.Value.ShouldBe(6.1250m); fact.Unit.ShouldBe("mmol/L"); fact.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T08:00:05Z"));
        fact.TimeZoneSnapshot.ShouldBe("Europe/Berlin"); fact.ValueUnitSource.ShouldBe("image/batch_default"); fact.OccurredAtSource.ShouldBe("batch_year");
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task ReviewFix_maximum_caption_surrogate_boundary_is_lossless_and_all_complete_pages_authorize_exact_fact()
    {
        await SeedAsync(); await using var s=Open(); var caption=new string('a',2999)+"\U0001F9EA"+new string('b',1095); caption.Length.ShouldBe(4096);
        var source=await Original(s,1,caption:caption); var w=await Start(s,[source]); await Finish(s,w,0);
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic maximum supported caption",900),w.Run.Id,Ct);
        var reviews=await s.Context.Set<VetPhotoReview>().AsNoTracking().Where(r=>r.Kind=="reextract_comparison"&&r.State=="preview").ToListAsync(Ct);
        reviews.Count.ShouldBe(1); var review=reviews.Single();
        ReviewFixAssertAllPagesDelivered(review,s.Telegram); var pages=JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson,Json)!;
        var parts=pages.SelectMany(page=>page.Split("\n\n")).Where(block=>block.StartsWith("Неизменяемая подпись выбранного входа (данные) — часть ",StringComparison.Ordinal)).ToArray();
        parts.Length.ShouldBe(2); parts[0].ShouldStartWith("Неизменяемая подпись выбранного входа (данные) — часть 1/2\n");
        parts[1].ShouldStartWith("Неизменяемая подпись выбранного входа (данные) — часть 2/2\n");
        string.Concat(parts.Select(block=>block[(block.IndexOf('\n')+1)..])).ShouldBe(caption);
        foreach(var page in pages)System.Text.Encoding.UTF8.GetString(new System.Text.UTF8Encoding(false,true).GetBytes(page)).ShouldBe(page);
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct)).Value.ShouldBe(6.1250m);
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task ReviewFix_expanded_supported_captions_refuse_the_whole_over64_page_review_without_partial_authority()
    {
        await SeedAsync(); await using var s=Open(); var sources=new List<VetPhotoAdmission>();
        for(var id=1;id<=27;id++)sources.Add(await Original(s,id,caption:new string('x',4096)));
        var w=await Start(s,sources); for(var i=0;i<27;i++)await Finish(s,w,i); s.Telegram.ClearSent();
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic complete oversized comparison",900),w.Run.Id,Ct);
        string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text)).ShouldContain("Полный просмотр не помещается");
        (await s.Context.Set<VetPhotoReview>().CountAsync(r=>r.Kind=="reextract_comparison",Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBeNull();
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(27); (await Store(s).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(0L);
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewFix_failed_initial_or_comparison_preview_holds_on_repeated_resume_until_explicit_show(bool comparison)
    {
        await SeedAsync(); await using var s=Open(); var one=await Original(s,1); VetPhotoReview failed; Window? w=null;
        s.Telegram.ThrowOnSendToChatId=Scope.ChatId;
        if(comparison)
        {
            w=await Start(s,[one,await Original(s,2)]); await Finish(s,w,0); await Finish(s,w,1,"unknown");
            await App(s).ResumeAsync(Bot,s.Telegram,Ct);
            var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);
            failed=(await Store(s).ReadPreviewAsync(Scope,window.ComparisonReviewId.ShouldNotBeNull(),111,Ct)).ShouldNotBeNull();
        }
        else
        {
            var initial=await ReviewFixStageInitial(s); var photos=Store(s); var composer=new VetPhotoReviewComposer(photos,photos,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);
            (await composer.DeliverAsync(Scope,initial,111,s.Telegram,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
            failed=(await Store(s).ReadPreviewAsync(Scope,initial.Id,111,Ct)).ShouldNotBeNull();
        }
        failed.State.ShouldBe("preview_failed"); failed.CompletePreviewDelivered.ShouldBeFalse(); s.Telegram.ThrowOnSendToChatId=null;
        var count=s.Telegram.SentMessages.Count; var attempts=await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id));
        var guarded=Store(s); var heldComposer=new VetPhotoReviewComposer(guarded,guarded,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);
        (await heldComposer.DeliverAsync(Scope,failed,111,s.Telegram,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        await App(s).ResumeAsync(Bot,s.Telegram,Ct); await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        s.Telegram.SentMessages.Count.ShouldBe(count); (await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id))).ShouldBe(attempts);
        var held=(await Store(s).ReadPreviewAsync(Scope,failed.Id,111,Ct)).ShouldNotBeNull();
        held.Revision.ShouldBe(failed.Revision); held.State.ShouldBe("preview_failed");
        held.DeliveredPagesJson.ShouldBe(failed.DeliveredPagesJson); held.CompletePreviewDelivered.ShouldBeFalse();
        var runId=comparison?w!.Run.Id:(await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).Id;
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic explicit review retry",901),runId,Ct);
        var retried=(await Store(s).ReadPreviewAsync(Scope,failed.Id,111,Ct)).ShouldNotBeNull(); retried.Revision.ShouldBe(failed.Revision+1);
        ReviewFixAssertAllPagesDelivered(retried,s.Telegram); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        if(comparison){(await Store(s).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(1L); (await Accept(s,retried)).Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(1);}
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewFix_cancelled_send_after_transport_delivery_durably_holds_initial_or_comparison_without_resend(bool comparison)
    {
        await SeedAsync(); await using var s=Open(); var original=await Original(s,1); var initial=comparison?null:await ReviewFixStageInitial(s);
        Window? w=null; if(comparison){w=await Start(s,[original]);await Finish(s,w,0);}
        using var cancel=new CancellationTokenSource(); var client=new ReviewFixCancelClient(s.Telegram,cancel);
        if(comparison)await Should.ThrowAsync<OperationCanceledException>(()=>App(s).ResumeAsync(Bot,client,cancel.Token));
        else{var photos=Store(s);var composer=new VetPhotoReviewComposer(photos,photos,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);
            await Should.ThrowAsync<OperationCanceledException>(()=>composer.DeliverAsync(Scope,initial!,111,client,null,cancel.Token));}
        var id=initial?.Id??(await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldNotBeNull();
        var failed=(await Store(s).ReadPreviewAsync(Scope,id,111,Ct)).ShouldNotBeNull(); failed.State.ShouldBe("preview_failed");
        failed.CompletePreviewDelivered.ShouldBeFalse(); failed.AcceptancePromptMessageId.ShouldBeNull();
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(failed.DeliveredPagesJson,Json)!.ShouldBeEmpty(); client.Sends.ShouldBe(1);
        var count=s.Telegram.SentMessages.Count; await App(s).ResumeAsync(Bot,s.Telegram,Ct); await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        s.Telegram.SentMessages.Count.ShouldBe(count); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        var held=(await Store(s).ReadPreviewAsync(Scope,id,111,Ct)).ShouldNotBeNull();
        held.Revision.ShouldBe(failed.Revision); held.State.ShouldBe("preview_failed"); held.DeliveredPagesJson.ShouldBe(failed.DeliveredPagesJson);
        held.CompletePreviewDelivered.ShouldBeFalse(); held.AcceptancePromptMessageId.ShouldBeNull();
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task ReviewFix_more_than_five_failed_old_runs_do_not_hide_a_later_unstarted_complete_scope_preview()
    {
        await SeedAsync(); await using var s=Open(); await Original(s,1); var failedIds=new List<Guid>();
        for(var i=0;i<6;i++)
        {Clock.UtcNow=Now.AddSeconds(i);var r=await ReviewFixStageInitial(s);failedIds.Add(r.Id);
         (await Store(s).RecordPreviewFailureAsync(new(Scope,r.Id,r.Revision,r.OperationKey,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);}
        Clock.UtcNow=Now.AddSeconds(6);var valid=await ReviewFixStageInitial(s);var validRun=await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r=>r.SelectionReviewId==valid.Id,Ct);
        var due=await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct);due.Select(r=>r.RunId).ShouldBe(new[]{validRun.Id});
        await App(s).ResumeAsync(Bot,s.Telegram,Ct);var shown=(await Store(s).ReadPreviewAsync(Scope,valid.Id,111,Ct)).ShouldNotBeNull();ReviewFixAssertAllPagesDelivered(shown,s.Telegram);
        (await s.Context.Set<VetPhotoReview>().CountAsync(r=>failedIds.Contains(r.Id)&&r.State=="preview_failed"&&r.Revision==1&&!r.CompletePreviewDelivered,Ct)).ShouldBe(6);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(0);
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task ReviewFix_known_recorded_comparison_page_after_fatal_interruption_resumes_only_missing_pages_without_resetting_revision()
    {
        await SeedAsync(); var fault=new FenceSqlFault("second-marker-fatal"); await using var s=Open(interceptor:fault); var sources=new List<VetPhotoAdmission>();
        for(var i=1;i<=3;i++)sources.Add(await Original(s,i)); var w=await Start(s,sources);
        for(var i=0;i<3;i++)await Finish(s,w,i);
        fault.Armed=true;
        await Should.ThrowAsync<OutOfMemoryException>(()=>App(s).ResumeAsync(Bot,s.Telegram,Ct));
        fault.Armed=false; fault.Hits.ShouldBe(1);
        var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);
        var r=(await Store(s).ReadPreviewAsync(Scope,window.ComparisonReviewId.ShouldNotBeNull(),111,Ct)).ShouldNotBeNull();
        var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!; pages.Length.ShouldBeGreaterThan(1);
        r.State.ShouldBe("preview"); r.CompletePreviewDelivered.ShouldBeFalse();
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(r.DeliveredPagesJson,Json)!.Length.ShouldBe(1);
        s.Telegram.SentMessages.Count.ShouldBe(1);
        await App(s).ResumeAsync(Bot,s.Telegram,Ct);var shown=(await Store(s).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();
        shown.Revision.ShouldBe(r.Revision);s.Telegram.SentMessages.Count.ShouldBe(pages.Length);s.Telegram.SentMessages.Count(m=>m.Text==pages[0]).ShouldBe(1);
        ReviewFixAssertAllPagesDelivered(shown,s.Telegram);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);s.Chat.RequestedMessages.ShouldBeEmpty();
    }
    private sealed class ReviewFixCancelClient(FakeTelegramClient inner, CancellationTokenSource cancel) : ITelegramClient
    {
        public List<(int MessageId, string Text)> Delivered { get; } = [];
        public List<(long Chat, int Message, IReadOnlyList<InlineButton> Buttons)> EditedButtons { get; } = [];
        public int Sends { get; private set; }
        public async Task<int> SendTextAsync(long chat, int? topic, string text, int? reply, CancellationToken ct)
        {
            Sends++;
            var id = await inner.SendTextAsync(chat, topic, text, reply, ct); Delivered.Add((id, text));
            cancel.Cancel(); throw new OperationCanceledException(ct);
        }
        public Task EditMessageButtonsAsync(long chat, int message, IReadOnlyList<InlineButton> buttons, CancellationToken ct)
        {
            EditedButtons.Add((chat, message, buttons)); return inner.EditMessageButtonsAsync(chat, message, buttons, ct);
        }
        public Task<long> DownloadFileAsync(string id, Stream destination, long max, CancellationToken ct) => inner.DownloadFileAsync(id, destination, max, ct);
        public Task<BotIdentity> GetMeAsync(CancellationToken ct) => inner.GetMeAsync(ct);
        public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeout, IReadOnlyList<UpdateKind> kinds, CancellationToken ct) => inner.GetUpdatesAsync(offset, timeout, kinds, ct);
        public Task SendChatActionAsync(long chat, int? topic, string action, CancellationToken ct) => inner.SendChatActionAsync(chat, topic, action, ct);
        public Task SetReactionAsync(long chat, int message, string? emoji, CancellationToken ct) => inner.SetReactionAsync(chat, message, emoji, ct);
        public Task<int> SendTextWithButtonsAsync(long chat, int? topic, string text, IReadOnlyList<InlineButton> buttons, int? reply, CancellationToken ct) => inner.SendTextWithButtonsAsync(chat, topic, text, buttons, reply, ct);
        public Task EditMessageTextAsync(long chat, int message, string text, CancellationToken ct) => inner.EditMessageTextAsync(chat, message, text, ct);
        public Task AnswerCallbackAsync(string id, string? text, CancellationToken ct) => inner.AnswerCallbackAsync(id, text, ct);
        public Task<string> GetManagedBotTokenAsync(long id, CancellationToken ct) => inner.GetManagedBotTokenAsync(id, ct);
    }

    [Theory]
    [InlineData("unknown")][InlineData("failed")][InlineData("invalid")]
    public async Task Full_comparison_saves_successful_sibling_and_keeps_terminal_exception_with_exact_accounting(string sibling)
    {
        await SeedAsync();await using var s=Open();var one=await Original(s,1);var two=await Original(s,2);var w=await Start(s,[one,two]);
        var good=await Finish(s,w,0);var uncertain=await Finish(s,w,1,sibling);var before=(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c=>c.SourceId==uncertain.Claim.SourceId,Ct));
        var jsonBefore=JsonSerializer.Serialize(before,Json);var review=await Show(s,w);var items=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!;
        items.Length.ShouldBe(2);items.Single(i=>i.SourceId==good.Claim.SourceId).Disposition.ShouldBe("save");
        items.Single(i=>i.SourceId==uncertain.Claim.SourceId).Disposition.ShouldBe("keep");
        string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text)).ShouldContain(uncertain.Claim.AttemptKey.ToString("D"));
        var applied=await Accept(s,review);applied.Status.ShouldBe(VetMutationStatus.Applied);applied.EventIds.Count.ShouldBe(1);
        var fact=await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct);fact.Value.ShouldBe(6.1250m);fact.OccurredAt.Second.ShouldBe(5);
        fact.ExtractionResultId.ShouldBe(good.Result!.Id);fact.InputRevisionId.ShouldBe(good.Claim.InputRevisionId);fact.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(Ct)).ActorUserId.ShouldBe(222);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c=>c.Id==before.Id,Ct),Json).ShouldBe(jsonBefore);
        var attempt=await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a=>a.Id==uncertain.Claim.AttemptKey,Ct);
        attempt.State.ShouldBe(sibling=="invalid"?"failed":sibling);attempt.ReservedResultSlot.ShouldBe(sibling=="unknown");
        (await Store(s).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(sibling=="unknown"?1L:0L);
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await Store(s).ReconcileWindowAsync(new(Scope,w.Run.Id,222),w.Value.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("completed");
    }
    [Fact]
    public async Task All_unknown_full_keep_is_auditable_no_change_and_retains_both_charged_slots()
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1),await Original(s,2)]);await Finish(s,w,0,"unknown");await Finish(s,w,1,"unknown");
        var before=await Candidates(s);var review=await Show(s,w);JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.All(x=>x.Disposition=="keep"&&x.State==null).ShouldBeTrue();
        var applied=await Accept(s,review);applied.Status.ShouldBe(VetMutationStatus.NoChange);applied.EventIds.ShouldBeEmpty();
        (await Candidates(s)).ShouldBe(before);(await Store(s).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(2L);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(Ct)).Kind.ShouldBe("photo_comparison_keep");
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
    }
    [Theory]
    [InlineData("queued")][InlineData("claimed")][InlineData("dispatched")]
    public async Task Live_attempts_hold_window_without_review_fact_or_second_attempt(string state)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);var key=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!.Single().AttemptKey;
        if(state!="queued"){var claim=(await Store(s).ClaimScheduledImageAsync(Scope,key,111,Ct)).Claim.ShouldNotBeNull();if(state=="dispatched")(await Store(s).MarkImageDispatchedAsync(Scope,key,claim.ClaimToken,111,Ct)).ShouldBeTrue();}
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic show"),w.Run.Id,Ct);await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBeNull();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(1);(await s.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(1);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a=>a.Id==key,Ct)).State.ShouldBe(state);
    }
    [Theory]
    [InlineData("unknown",false)][InlineData("failed",true)][InlineData("returned",true)]
    public async Task Inconsistent_terminal_reservation_cannot_attach_a_full_comparison(string state,bool reservation)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);var done=await Finish(s,w,0,state);
        await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==done.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.ReservedResultSlot,reservation),Ct);
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic show"),w.Run.Id,Ct);
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBeNull();(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("actor")][InlineData("attempt_input")][InlineData("attempt_source")][InlineData("attempt_expected_current")]
    [InlineData("attempt_ordinal")][InlineData("window_manifest")][InlineData("run_manifest")][InlineData("approval_manifest")]
    [InlineData("approval_actor")][InlineData("approval_profile")][InlineData("approval_incomplete")][InlineData("window_ordinal")]
    public async Task Approved_manifest_and_exact_selected_attempt_fence_every_current_keep_or_save(string fence)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1),await Original(s,2)]);var one=await Finish(s,w,0);await Finish(s,w,1,"unknown");var review=await Show(s,w);
        var before=await Candidates(s);var other=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!.Single(x=>x.AttemptKey!=one.Claim.AttemptKey);
        switch(fence)
        {
            case "actor":await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==one.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.ActorUserId,222),Ct);break;
            case "attempt_input":await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==one.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.InputRevisionId,other.InputRevisionId),Ct);break;
            case "attempt_source":await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==one.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.SourceId,other.SourceId),Ct);break;
            case "attempt_expected_current":await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==one.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.ExpectedCurrentInputId,Guid.NewGuid()),Ct);break;
            case "attempt_ordinal":await s.Context.Set<VetPhotoAttempt>().Where(a=>a.Id==one.Claim.AttemptKey).ExecuteUpdateAsync(u=>u.SetProperty(a=>a.ExpectedSourceOrdinal,99),Ct);break;
            case "window_manifest":await s.Context.Set<VetPhotoRunWindow>().Where(x=>x.Id==w.Value.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.SelectionJson,"[]"),Ct);break;
            case "run_manifest":await s.Context.Set<VetPhotoRun>().Where(x=>x.Id==w.Run.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.SelectionJson,"[]"),Ct);break;
            case "approval_manifest":await s.Context.Set<VetPhotoReview>().Where(x=>x.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.SelectionJson,"[]"),Ct);break;
            case "approval_actor":await s.Context.Set<VetPhotoReview>().Where(x=>x.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.DecisionActorUserId,(long?)222),Ct);break;
            case "approval_profile":await s.Context.Set<VetPhotoReview>().Where(x=>x.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.ProfileId,(long?)null),Ct);break;
            case "approval_incomplete":await s.Context.Set<VetPhotoReview>().Where(x=>x.Id==w.Run.SelectionReviewId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.CompletePreviewDelivered,false),Ct);break;
            case "window_ordinal":await s.Context.Set<VetPhotoRunWindow>().Where(x=>x.Id==w.Value.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.Ordinal,1),Ct);break;
        }
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Stale);(await Candidates(s)).ShouldBe(before);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Id==review.Id,Ct)).State.ShouldBe("preview");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Late_unknown_response_is_explicit_fresh_correction_after_completed_or_cancelled_window(bool cancel)
    {
        await SeedAsync();await using var s=Open();var original=await Original(s,1);var w=await Start(s,[original]);var unknown=await Finish(s,w,0,"unknown");
        if(cancel)(await Store(s).CancelRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        else{var keep=await Show(s,w);(await Accept(s,keep)).Status.ShouldBe(VetMutationStatus.NoChange);(await Store(s).ReconcileWindowAsync(new(Scope,w.Run.Id,222),w.Value.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);}
        var oldWindow=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);var oldReviews=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(r=>r.Id));
        var pointer=await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);
        var completed=await Store(s).CompleteImageAsync(new(Scope,unknown.Claim.AttemptKey,unknown.Claim.ClaimToken,111,unknown.Claim.SourceId,unknown.Claim.InputRevisionId,"gpt-6.1-sol",Result(unknown.Claim.SourceId,unknown.Claim.InputRevisionId,0)),Ct);
        completed.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);var result=completed.Extraction.ShouldNotBeNull();
        await App(s).ResumeAsync(Bot,s.Telegram,Ct);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic exact result"),w.Run.Id,w.Value.Id,unknown.Claim.SourceId,unknown.Claim.InputRevisionId,result.Id,false,Ct);
        var fresh=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Kind=="correction",Ct);fresh.RunWindowId.ShouldBe(w.Value.Id);fresh.CompletePreviewDelivered.ShouldBeTrue();
        (await Accept(s,fresh)).Status.ShouldBe(VetMutationStatus.Applied);(await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct)).ExtractionResultId.ShouldBe(result.Id);
        var after=await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);after.CurrentInputRevisionId.ShouldBe(pointer.CurrentInputRevisionId);after.CurrentOrdinal.ShouldBe(pointer.CurrentOrdinal);
        var finalWindow=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);finalWindow.State.ShouldBe(oldWindow.State);finalWindow.ComparisonReviewId.ShouldBe(oldWindow.ComparisonReviewId);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoReview>().AsNoTracking().Where(r=>r.Id!=fresh.Id).OrderBy(r=>r.Id).ToListAsync(Ct),Json).ShouldBe(oldReviews);
    }
    [Fact]
    public async Task Accepted_comparison_reconciles_after_profile_refresh_but_cannot_continue_new_calls()
    {
        await SeedAsync();await using var s=Open();var originals=new List<VetPhotoAdmission>();for(var id=1;id<=51;id++)originals.Add(await Original(s,id));var w=await Start(s,originals);
        for(var i=0;i<50;i++)await Finish(s,w,i,"unknown");
        var review=await Show(s,w);(await Accept(s,review)).Status.ShouldBe(VetMutationStatus.NoChange);
        var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);(await s.Profiles.UpdateAsync(FamilyId,Bot.BotDbId,111,p.Revision,[new("TimeZone","Europe/London")],Ct)).Applied.ShouldBeTrue();
        (await Store(s).ReconcileWindowAsync(new(Scope,w.Run.Id,222),w.Value.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).ContinueRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(50);
        (await Store(s).CancelRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).GetRunAsync(new(Scope,w.Run.Id,111),0,5,Ct)).ShouldNotBeNull().Run.State.ShouldBe("cancelled");
        (await Store(s).GetCapacityAsync(Scope,111,Ct)).ReservedResults.ShouldBe(50L);
    }
    [Fact]
    public async Task Pending_measurement_clock_is_fully_shown_keep_without_upload_time_substitution()
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);await Finish(s,w,0,missingClock:true);var review=await Show(s,w);
        var item=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.Single();item.Disposition.ShouldBe("keep");item.State.ShouldBeNull();
        string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text)).ShouldContain("missing_or_invalid_measurement_time");
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.NoChange);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Protected_cancelled_source_requires_explicit_restore_for_late_comparison_choice()
    {
        await SeedAsync();await using var s=Open();var original=await Original(s,1,protect:true);var w=await Start(s,[original]);var unknown=await Finish(s,w,0,"unknown");
        (await Store(s).CancelRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var result=(await Store(s).CompleteImageAsync(new(Scope,unknown.Claim.AttemptKey,unknown.Claim.ClaimToken,111,unknown.Claim.SourceId,unknown.Claim.InputRevisionId,"gpt-6.1-sol",Result(unknown.Claim.SourceId,unknown.Claim.InputRevisionId,0)),Ct)).Extraction.ShouldNotBeNull();
        var before=await Candidates(s);await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic bare result"),w.Run.Id,w.Value.Id,unknown.Claim.SourceId,unknown.Claim.InputRevisionId,result.Id,false,Ct);
        (await Candidates(s)).ShouldBe(before);(await s.Context.Set<VetPhotoReview>().CountAsync(r=>r.Kind=="correction",Ct)).ShouldBe(0);
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic explicit restore"),w.Run.Id,w.Value.Id,unknown.Claim.SourceId,unknown.Claim.InputRevisionId,result.Id,true,Ct);
        var review=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Kind=="correction",Ct);JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.Single().ExplicitRestoration.ShouldBeTrue();
        (await Accept(s,review)).Status.ShouldBe(VetMutationStatus.Applied);(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct)).RequiresExplicitRestoration.ShouldBeFalse();
    }
    [Theory]
    [InlineData("topic")][InlineData("chat")][InlineData("telegram")][InlineData("member")][InlineData("place")]
    public async Task Role_local_evidence_and_exact_result_selection_are_private_to_receiving_scope(string fence)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);var result=await Finish(s,w,0);var scope=Scope;
        if(fence=="topic")scope=scope with{TopicId=8};if(fence=="chat")scope=scope with{ChatId=-200};if(fence=="telegram")scope=scope with{TelegramBotId=2001};
        if(fence=="member")await s.Context.FamilyMembers.Where(m=>m.TelegramUserId==111).ExecuteUpdateAsync(u=>u.SetProperty(m=>m.Status,FamilyMemberStatus.Denied),Ct);
        if(fence=="place")await s.Context.Places.Where(p=>p.BotId==Bot.BotDbId&&p.ChatId==Scope.ChatId&&p.TopicId==Scope.TopicId).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.Status,PlaceStatus.Denied),Ct);
        if(fence=="telegram")
        {
            var read=await Should.ThrowAsync<InvalidOperationException>(()=>Store(s).ReadWindowAsync(new(scope,w.Run.Id,111),w.Value.Id,Ct));read.Message.ShouldBe("Vet bot scope is invalid.");
            var lookup=await Should.ThrowAsync<InvalidOperationException>(()=>Store(s).FindAttemptRunAsync(scope,result.Claim.AttemptKey,111,Ct));lookup.Message.ShouldBe("Vet bot scope is invalid.");
        }
        else
        {(await Store(s).ReadWindowAsync(new(scope,w.Run.Id,111),w.Value.Id,Ct)).ShouldBeNull();(await Store(s).FindAttemptRunAsync(scope,result.Claim.AttemptKey,111,Ct)).ShouldBeNull();}
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
    }
    private async Task<VetPhotoReview> Alternative(VetTestSession s,Window w,VetPhotoReview prior)
    {
        var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        var items=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(prior.SelectionJson,Json)!;
        var preview=VetPhotoReviewFormatter.FormatBlocks("synthetic complete alternative comparison",items.Select(i=>JsonSerializer.Serialize(i,Json)).ToArray(),"synthetic exact full selected values and bindings");preview.Success.ShouldBeTrue();
        var staged=await Store(s).StageReviewAsync(new(Scope,Guid.NewGuid(),222,VetPhotoReviewKind.ReextractComparison,null,null,p.Revision,
            prior.SelectionJson,preview,w.Value.Id),Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);return staged.Review.ShouldNotBeNull();
    }
    [Theory]
    [InlineData("preview")][InlineData("stale")]
    public async Task Explicit_replacement_invalidates_only_exact_unaccepted_prior_authority_and_delivers_fresh_review(string priorState)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);await Finish(s,w,0);var old=await Show(s,w);
        if(priorState=="stale")await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==old.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.State,"stale"),Ct);
        var next=await Alternative(s,w,old);var before=await Candidates(s);
        var changed=await Store(s).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,old.Id,old.Revision,next.Id,Ct);
        changed.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);(await Candidates(s)).ShouldBe(before);
        var original=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Id==old.Id,Ct);
        original.State.ShouldBe("stale");original.CompletePreviewDelivered.ShouldBeFalse();
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBe(next.Id);
        (await Accept(s,old)).Status.ShouldBe(VetMutationStatus.Stale);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        await Delivered(s,next);next=(await Store(s).ReadPreviewAsync(Scope,next.Id,222,Ct)).ShouldNotBeNull();
        (await Accept(s,next)).Status.ShouldBe(VetMutationStatus.Applied);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await Store(s).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,old.Id,old.Revision,next.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
    }
    [Theory]
    [InlineData("prior_id")][InlineData("prior_revision")][InlineData("accepted")][InlineData("declined")]
    [InlineData("completed")][InlineData("cancelled")][InlineData("new_profile")][InlineData("new_kind")][InlineData("new_window")]
    public async Task Replacement_never_overrides_another_revision_decision_or_window(string fence)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);await Finish(s,w,0);var old=await Show(s,w);var next=await Alternative(s,w,old);
        if(fence=="accepted")(await Accept(s,old)).Status.ShouldBe(VetMutationStatus.Applied);
        if(fence=="declined")await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==old.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.State,"declined"),Ct);
        if(fence is "completed" or "cancelled")await s.Context.Set<VetPhotoRunWindow>().Where(x=>x.Id==w.Value.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.State,fence),Ct);
        if(fence=="new_profile")await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==next.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.ProfileRevision,99),Ct);
        if(fence=="new_kind")await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==next.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Kind,"save"),Ct);
        if(fence=="new_window")await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==next.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.RunWindowId,(Guid?)null),Ct);
        var before=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(r=>r.Id));var candidates=await Candidates(s);var facts=await s.Context.Set<VetEvent>().CountAsync(Ct);
        (await Store(s).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,fence=="prior_id"?Guid.NewGuid():old.Id,
            fence=="prior_revision"?old.Revision+1:old.Revision,next.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(r=>r.Id))).ShouldBe(before);(await Candidates(s)).ShouldBe(candidates);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(facts);(await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBe(old.Id);
    }
    [Theory]
    [InlineData("source")][InlineData("input")][InlineData("candidate_revision")][InlineData("candidate_result")]
    [InlineData("result")][InlineData("original")][InlineData("missing_item")]
    public async Task New_replacement_selection_must_bind_every_source_candidate_and_exact_scheduled_result(string fence)
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1),await Original(s,2)]);await Finish(s,w,0);await Finish(s,w,1);
        var old=await Show(s,w);var next=await Alternative(s,w,old);var items=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(next.SelectionJson,Json)!;
        items[0]=fence switch
        {
            "source"=>items[0] with{SourceId=items[1].SourceId},"input"=>items[0] with{InputRevisionId=items[1].InputRevisionId},
            "candidate_revision"=>items[0] with{CandidateRevision=items[0].CandidateRevision+1},
            "candidate_result"=>items[0] with{ExpectedCandidateExtractionId=Guid.NewGuid()},
            "result"=>items[0] with{ExtractionResultId=items[1].ExtractionResultId},
            "original"=>items[0] with{OriginalReferenceId=items[1].OriginalReferenceId},_=>items[0]
        };
        var text=JsonSerializer.Serialize(fence=="missing_item"?items.Take(1).ToArray():items,Json);
        await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==next.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.SelectionJson,text).SetProperty(r=>r.Fingerprint,Hash(text)),Ct);
        var before=await Candidates(s);
        (await Store(s).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,old.Id,old.Revision,next.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBe(old.Id);
        (await Store(s).ReadPreviewAsync(Scope,old.Id,111,Ct)).ShouldNotBeNull().CompletePreviewDelivered.ShouldBeTrue();
        (await Candidates(s)).ShouldBe(before);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Two_member_acceptance_racing_exact_replacement_has_one_first_valid_authority(bool replaceFirst)
    {
        await SeedAsync();await using var seed=Open();var w=await Start(seed,[await Original(seed,1)]);await Finish(seed,w,0);var old=await Show(seed,w);var next=await Alternative(seed,w,old);await Delivered(seed,next);
        var hold=new HoldWrite(replaceFirst?"UPDATE vet_photo_reviews":"INSERT INTO vet_diary_actions");var observe=new ObserveBotLock();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var first=Open(interceptor:hold);await using var second=Open(interceptor:observe);
        Task<VetMutationResult>? accepting=null;Task<VetPhotoRunChange>? replacing=null;
        try
        {
            if(replaceFirst)replacing=Store(first).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,old.Id,old.Revision,next.Id,timeout.Token);
            else accepting=first.Diary.ApplyPhotoReviewAsync(new(Scope,old.Id,old.Revision,old.OperationKey,111,old.AcceptancePromptMessageId),timeout.Token);
            await hold.Entered.Task.WaitAsync(timeout.Token);
            if(replaceFirst)accepting=second.Diary.ApplyPhotoReviewAsync(new(Scope,old.Id,old.Revision,old.OperationKey,111,old.AcceptancePromptMessageId),timeout.Token);
            else replacing=Store(second).ReplaceComparisonAsync(new(Scope,w.Run.Id,222),w.Value.Id,old.Id,old.Revision,next.Id,timeout.Token);
            await observe.Entered.Task.WaitAsync(timeout.Token);
        }
        finally{hold.Release();}
        await Task.WhenAll(accepting.ShouldNotBeNull(),replacing.ShouldNotBeNull());
        var accepted=await accepting.ShouldNotBeNull();var replaced=await replacing.ShouldNotBeNull();
        await using var check=Open();var winner=await check.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);
        if(!replaceFirst)
        {accepted.Status.ShouldBe(VetMutationStatus.Applied);replaced.Status.ShouldBe(VetPhotoWorkflowStatus.Stale);winner.ComparisonReviewId.ShouldBe(old.Id);}
        else
        {accepted.Status.ShouldBe(VetMutationStatus.Stale);replaced.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);winner.ComparisonReviewId.ShouldBe(next.Id);
         next=(await Store(check).ReadPreviewAsync(Scope,next.Id,222,Ct)).ShouldNotBeNull();(await Accept(check,next,222)).Status.ShouldBe(VetMutationStatus.Applied);}
        (await check.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(1);(await check.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await check.Context.Set<VetPhotoReview>().CountAsync(r=>r.State=="accepted"&&r.Kind=="reextract_comparison",Ct)).ShouldBe(1);
    }
    [Fact]
    public async Task Profile_refresh_holds_run_and_explicit_exact_result_stages_fresh_correction_without_rewriting_window_or_attempt()
    {
        await SeedAsync();await using var s=Open();var w=await Start(s,[await Original(s,1)]);var result=await Finish(s,w,0);var old=await Show(s,w);
        var attempts=await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id));
        var sources=await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking().OrderBy(x=>x.Id));
        var capacity=JsonSerializer.Serialize(await Store(s).GetCapacityAsync(Scope,111,Ct),Json);
        var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);var priorRevision=p.Revision;(await s.Profiles.UpdateAsync(FamilyId,Bot.BotDbId,111,priorRevision,[new("TimeZone","Europe/London")],Ct)).Applied.ShouldBeTrue();
        await s.Context.Set<VetPhotoReview>().Where(r=>r.Id==old.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.State,"stale").SetProperty(r=>r.CompletePreviewDelivered,false),Ct);
        await App(s).ShowAsync(Bot,s.Telegram,Text("synthetic show"),w.Run.Id,Ct);(await s.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(2);
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic exact result"),w.Run.Id,w.Value.Id,result.Claim.SourceId,result.Claim.InputRevisionId,result.Result!.Id,false,Ct);
        var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);window.ComparisonReviewId.ShouldBe(old.Id);window.SelectionJson.ShouldBe(w.Value.SelectionJson);
        var held=await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct);held.State.ShouldBe("stale");held.SelectionJson.ShouldBe(w.Run.SelectionJson);held.NextWindowOrdinal.ShouldBe(w.Run.NextWindowOrdinal);
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Id==old.Id,Ct)).State.ShouldBe("stale");
        var fresh=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r=>r.Kind=="correction",Ct);fresh.Id.ShouldNotBe(old.Id);
        fresh.ProfileId.ShouldBe(p.Id);fresh.ProfileRevision.ShouldBe(priorRevision+1);fresh.RunWindowId.ShouldBe(w.Value.Id);fresh.CompletePreviewDelivered.ShouldBeTrue();
        var selected=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(fresh.SelectionJson,Json)!.Single();
        selected.SourceId.ShouldBe(result.Claim.SourceId);selected.InputRevisionId.ShouldBe(result.Claim.InputRevisionId);selected.ExtractionResultId.ShouldBe(result.Result.Id);
        selected.ProfileRevision.ShouldBe(priorRevision+1);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        (await Accept(s,fresh)).Status.ShouldBe(VetMutationStatus.Applied);
        var fact=await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct);fact.Value.ShouldBe(6.1250m);fact.Unit.ShouldBe("mmol/L");fact.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T10:00:05Z",CultureInfo.InvariantCulture));
        fact.PhotoSourceId.ShouldBe(result.Claim.SourceId);fact.InputRevisionId.ShouldBe(result.Claim.InputRevisionId);fact.ExtractionResultId.ShouldBe(result.Result.Id);fact.SourceAuthorUserId.ShouldBe(111);fact.SourceKind.ShouldBe("photo");
        var action=await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(Ct);action.ActorUserId.ShouldBe(222);action.ChatId.ShouldBe(Scope.ChatId);action.TopicId.ShouldBe(Scope.TopicId);
        var facts=await Snapshot(s.Context.Set<VetEvent>().AsNoTracking());var candidates=await Candidates(s);
        (await Accept(s,fresh)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        (await Store(s).ReconcileWindowAsync(new(Scope,w.Run.Id,222),w.Value.Id,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(s).ContinueRunAsync(new(Scope,w.Run.Id,111),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s.Context.Set<VetEvent>().AsNoTracking())).ShouldBe(facts);(await Candidates(s)).ShouldBe(candidates);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id))).ShouldBe(attempts);(await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking().OrderBy(x=>x.Id))).ShouldBe(sources);
        JsonSerializer.Serialize(await Store(s).GetCapacityAsync(Scope,111,Ct),Json).ShouldBe(capacity);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("stale");
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldBe(old.Id);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(1);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Every_historic_input_is_shown_and_only_current_or_explicit_exact_choice_can_propose_one_source(bool selectedHasCurrent)
    {
        await SeedAsync();await using var s=Open();var first=await Original(s,1);var second=await Original(s,1,edit:true);
        if(!selectedHasCurrent)await Original(s,1,edit:true);
        var w=await Start(s,[first,second],VetPhotoRunSelectionMode.AllOriginals);
        var snapshots=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.Value.SelectionJson,Json)!;
        var results=new List<Dispatch>();for(var i=0;i<snapshots.Length;i++)results.Add(await Finish(s,w,i));
        // Two or three retained input revisions are all shown; the selected current one is preferred.
        var review=await Show(s,w);var shown=string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text));foreach(var snap in snapshots)shown.ShouldContain(snap.InputRevisionId.ToString("D"));
        var item=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.Single();
        var source=await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);item.InputRevisionId.ShouldBe(source.CurrentInputRevisionId);
        var historic=results.Single(r=>r.Claim.InputRevisionId==first.Input!.Id);var candidateBefore=await Candidates(s);
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic exact historical selection"),w.Run.Id,w.Value.Id,historic.Claim.SourceId,historic.Claim.InputRevisionId,historic.Result!.Id,false,Ct);
        var attached=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);attached.ComparisonReviewId.ShouldNotBe(review.Id);
        var chosen=(await Store(s).ReadPreviewAsync(Scope,attached.ComparisonReviewId!.Value,111,Ct)).ShouldNotBeNull();
        var selected=JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(chosen.SelectionJson,Json)!.Single();selected.InputRevisionId.ShouldBe(first.Input!.Id);
        selected.ExtractionResultId.ShouldBe(historic.Result.Id);(await Candidates(s)).ShouldBe(candidateBefore);
        (await Accept(s,chosen)).Status.ShouldBe(VetMutationStatus.Applied);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(1);
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(source.CurrentInputRevisionId);
    }
    [Fact]
    public async Task Two_selected_historic_alternatives_remain_keep_until_exact_result_choice_without_current_pointer_change()
    {
        await SeedAsync();await using var s=Open();var first=await Original(s,1);var second=await Original(s,1,edit:true);var current=await Original(s,1,edit:true);
        var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);var refs=await s.Context.Set<VetPhotoOriginalReference>().Where(r=>r.InputRevisionId==first.Input!.Id||r.InputRevisionId==second.Input!.Id).Select(r=>r.Id).ToArrayAsync(Ct);
        var staged=await Store(s).StageRunAsync(new(Scope,Guid.NewGuid(),111,p.Revision,VetPhotoRunPurpose.Reprocess,VetPhotoRunSelectionMode.Selected,"gpt-6.1-sol","codex-cli",ReferenceIds:refs),Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);await Delivered(s,staged.Review!);var initial=(await Store(s).ReadPreviewAsync(Scope,staged.Review!.Id,111,Ct)).ShouldNotBeNull();var h=new VetPhotoRunHandle(Scope,staged.Run!.Id,111);
        (await Store(s).ApproveRunAsync(h,new(Scope,initial.Id,initial.Revision,initial.OperationKey,111,initial.AcceptancePromptMessageId),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var continued=await Store(s).ContinueRunAsync(h,Ct);var w=new Window(continued.Run!,continued.Window!,[first,second]);var one=await Finish(s,w,0);await Finish(s,w,1);
        var review=await Show(s,w);JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson,Json)!.Single().Disposition.ShouldBe("keep");
        string.Join("\n",s.Telegram.SentMessages.Select(m=>m.Text)).ShouldContain("/photos_result");
        await App(s).SelectResultAsync(Bot,s.Telegram,Text("synthetic exact alternative"),w.Run.Id,w.Value.Id,one.Claim.SourceId,one.Claim.InputRevisionId,one.Result!.Id,false,Ct);
        var win=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);var choice=(await Store(s).ReadPreviewAsync(Scope,win.ComparisonReviewId!.Value,111,Ct)).ShouldNotBeNull();
        (await Accept(s,choice)).Status.ShouldBe(VetMutationStatus.Applied);(await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct)).InputRevisionId.ShouldBe(one.Claim.InputRevisionId);
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(current.Input!.Id);(await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(1);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Begin_composes_current_or_exact_selected_reference_with_upload_date_filter_and_full_approval_without_calls(bool selected)
    {
        await SeedAsync();await using var s=Open();var first=await Original(s,1);await Original(s,2);
        var op=new VetPhotoOperation("reprocess",null,null,null,selected?[first.Source!.Id]:[],[],null,[],null,null,
            selected?"selected":"current","2031-05-12","2031-05-12","upload",null,null,[]);
        await App(s).BeginAsync(Bot,s.Telegram,Text("synthetic requested area"),op,Guid.NewGuid(),Ct);
        var run=await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct);run.SelectionMode.ShouldBe(selected?"selected":"current");run.SelectedCount.ShouldBe(selected?1:2);
        var manifest=JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(run.SelectionJson,Json)!;
        if(selected){manifest.Single().SourceId.ShouldBe(first.Source!.Id);manifest.Single().InputRevisionId.ShouldBe(first.Input!.Id);}
        var review=(await Store(s).ReadPreviewAsync(Scope,run.SelectionReviewId,111,Ct)).ShouldNotBeNull();review.CompletePreviewDelivered.ShouldBeTrue();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(0);(await s.Context.Set<VetPhotoRunWindow>().CountAsync(Ct)).ShouldBe(0);
        (await App(s).ConfirmAsync(Scope,review,222,review.AcceptancePromptMessageId,s.Telegram,900,Ct)).ShouldBeTrue();
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("approved");(await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(Ct)).DecisionActorUserId.ShouldBe(222);
    }
    [Fact]
    public async Task Recovery_reads_only_five_authorized_metadata_runs_and_never_continues_or_creates_an_image_attempt()
    {
        await SeedAsync();await using var s=Open();await Original(s,1);var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        for(var i=0;i<7;i++)(await Store(s).StageRunAsync(new(Scope,Guid.NewGuid(),111,p.Revision,VetPhotoRunPurpose.Reprocess,VetPhotoRunSelectionMode.Current,"gpt-6.1-sol","codex-cli"),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var due=await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct);due.Count.ShouldBe(5);due.Select(r=>r.RunId).Distinct().Count().ShouldBe(5);due.All(r=>r.Scope==Scope&&r.ActorUserId==111).ShouldBeTrue();
        (await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,6,Ct)).ShouldBeEmpty();await App(s).ResumeAsync(Bot,s.Telegram,Ct);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a=>a.Kind=="image",Ct)).ShouldBe(0);(await s.Context.Set<VetPhotoRunWindow>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoReview>().CountAsync(r=>r.CompletePreviewDelivered,Ct)).ShouldBe(5);
        await s.Context.Places.Where(x=>x.BotId==Bot.BotDbId&&x.ChatId==Scope.ChatId&&x.TopicId==Scope.TopicId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.Status,PlaceStatus.Denied),Ct);
        (await Store(s).GetRecoverableRunsAsync(FamilyId,Bot.BotDbId,5,Ct)).ShouldBeEmpty();(await s.Context.Set<VetPhotoBlob>().CountAsync(b=>b.Content!=null,Ct)).ShouldBe(1);
    }
    [Fact]
    public async Task Deletion_full_selection_and_window_require_owner_confirmation_and_preserve_saved_facts_and_provenance()
    {
        await SeedAsync();await using var s=Open();var original=await Original(s,1);var image=await Start(s,[original]);await Finish(s,image,0);var saved=await Show(s,image);(await Accept(s,saved)).Status.ShouldBe(VetMutationStatus.Applied);
        var facts=await Snapshot(s.Context.Set<VetEvent>().AsNoTracking());var sources=await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking());var inputs=await Snapshot(s.Context.Set<VetPhotoInputRevision>().AsNoTracking());
        var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        var denied=await Store(s).StageRunAsync(new(Scope,Guid.NewGuid(),222,p.Revision,VetPhotoRunPurpose.DeleteOriginals,VetPhotoRunSelectionMode.Current,"",""),Ct);denied.Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        var op=new VetPhotoOperation("delete_originals",null,null,null,[],[],null,[],null,null,"current",null,null,"upload",null,null,[]);
        await App(s).BeginAsync(Bot,s.Telegram,Text("synthetic delete originals"),op,Guid.NewGuid(),Ct);
        var run=await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r=>r.SelectionMode=="deletion_current",Ct);var initial=(await Store(s).ReadPreviewAsync(Scope,run.SelectionReviewId,111,Ct)).ShouldNotBeNull();initial.CompletePreviewDelivered.ShouldBeTrue();
        (await App(s).ConfirmAsync(Scope,initial,111,initial.AcceptancePromptMessageId,s.Telegram,900,Ct)).ShouldBeTrue();(await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r=>r.State=="retained",Ct)).ShouldBe(1);
        await App(s).ContinueAsync(Bot,s.Telegram,Text("synthetic explicit deletion window"),run.Id,Ct);
        var window=await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(w=>w.RunId==run.Id,Ct);var review=(await Store(s).ReadPreviewAsync(Scope,window.ComparisonReviewId!.Value,111,Ct)).ShouldNotBeNull();review.CompletePreviewDelivered.ShouldBeTrue();
        var selection=JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(review.SelectionJson,Json)!.Single();string.Join("\n",JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson,Json)!).ShouldContain(selection.ReferenceId.ToString("D"));
        (await Store(s).ConfirmDeletionWindowAsync(new(Scope,run.Id,222),window.Id,new(Scope,review.Id,review.Revision,review.OperationKey,222,review.AcceptancePromptMessageId),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await App(s).ConfirmAsync(Scope,review,111,review.AcceptancePromptMessageId,s.Telegram,900,Ct)).ShouldBeTrue();
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r=>r.State=="deleted",Ct)).ShouldBe(1);
        (await Snapshot(s.Context.Set<VetEvent>().AsNoTracking())).ShouldBe(facts);(await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking())).ShouldBe(sources);(await Snapshot(s.Context.Set<VetPhotoInputRevision>().AsNoTracking())).ShouldBe(inputs);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r=>r.Id==run.Id,Ct)).State.ShouldBe("completed");
        (await App(s).ConfirmAsync(Scope,review,111,review.AcceptancePromptMessageId,s.Telegram,900,Ct)).ShouldBeTrue();(await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r=>r.State=="deleted",Ct)).ShouldBe(1);
    }
    private sealed class HoldWrite(string needle):DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release()=>release.TrySetResult(true);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {if(command.CommandText.Replace("\"","",StringComparison.Ordinal).Contains(needle,StringComparison.OrdinalIgnoreCase)){Entered.TrySetResult(true);await release.Task.WaitAsync(cancellationToken);}return result;}
    }
    private sealed class ObserveBotLock:DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {if(command.CommandText.Contains("pg_advisory_xact_lock",StringComparison.Ordinal)&&command.CommandText.Contains("vet:",StringComparison.Ordinal))Entered.TrySetResult(true);return ValueTask.FromResult(result);}
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

    private VetPhotoReviewComposer FenceComposer(VetTestSession s)
    {var p=Store(s);return new(p,p,s.Profiles,s.Diary,NullLogger<VetPhotoReviewComposer>.Instance);}
    private sealed record FenceBatch(VetPhotoReview Review,VetPhotoAdmission Source);
    private async Task<FenceBatch> FenceBatchPreview(VetTestSession s)
    {
        var source=await Original(s,1);var batch=(await Store(s).GetBatchAsync(Scope,source.Source!.BatchId!.Value,111,Ct)).ShouldNotBeNull();
        var profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
        var pages=new VetPhotoPreviewResult(new[]{"synthetic complete page one","synthetic complete page two","synthetic complete page three"},null);
        var selected=JsonSerializer.Serialize(new[]{new{sourceId=source.Source.Id,inputId=source.Input!.Id,candidateId=batch.Items.Single().Candidate.Id}},Json);
        var review=(await Store(s).StageReviewAsync(new(Scope,Guid.NewGuid(),111,VetPhotoReviewKind.Save,batch.Batch.Id,
            batch.Batch.ReviewRevision,profile.Revision,selected,pages),Ct)).Review.ShouldNotBeNull();
        return new(review,source);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewFence_successful_send_and_both_postsend_sql_failures_hold_after_restart_until_explicit_show(bool comparison)
    {
        await SeedAsync();var fault=new FenceSqlFault("post-send-double");await using var s=Open(interceptor:fault);
        var one=await Original(s,1);VetPhotoReview before;Window? window=null;
        if(comparison)
        {window=await Start(s,[one]);await Finish(s,window,0);before=null!;}
        else before=await ReviewFixStageInitial(s);
        var transport=new FenceTelegram(s.Telegram,(_,_)=>{fault.Armed=true;return Task.CompletedTask;});
        if(comparison)await App(s).ResumeAsync(Bot,transport,Ct);
        else(await FenceComposer(s).DeliverAsync(Scope,before,111,transport,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        fault.Hits.ShouldBe(2);transport.Delivered.Count.ShouldBe(1);s.Telegram.SentMessages.Count.ShouldBe(1);
        await using var restarted=Open();
        var id=comparison?(await restarted.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).ComparisonReviewId.ShouldNotBeNull():before.Id;
        var held=(await Store(restarted).ReadPreviewAsync(Scope,id,111,Ct)).ShouldNotBeNull();held.State.ShouldBe("preview_failed");
        held.DeliveredPagesJson.ShouldBe("[]");held.CompletePreviewDelivered.ShouldBeFalse();held.AcceptancePromptMessageId.ShouldBeNull();
        (await Store(restarted).GetReviewAsync(new(Scope,id,held.Revision,held.OperationKey,111,transport.Delivered[0].MessageId),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(restarted).FindNaturalReviewAsync(Scope,111,held.OperationKey,held.Revision,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.NotFound);
        (await Accept(restarted,held)).Status.ShouldBe(VetMutationStatus.Stale);
        var attempts=await Snapshot(restarted.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id));
        await App(restarted).ResumeAsync(Bot,s.Telegram,Ct);await App(restarted).ResumeAsync(Bot,s.Telegram,Ct);
        s.Telegram.SentMessages.Count.ShouldBe(1);
        var unchanged=(await Store(restarted).ReadPreviewAsync(Scope,id,111,Ct)).ShouldNotBeNull();
        unchanged.State.ShouldBe("preview_failed");unchanged.Revision.ShouldBe(held.Revision);unchanged.DeliveredPagesJson.ShouldBe("[]");
        (await Snapshot(restarted.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a=>a.Id))).ShouldBe(attempts);
        var runId=comparison?window!.Run.Id:(await restarted.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).Id;
        await App(restarted).ShowAsync(Bot,s.Telegram,Text("synthetic explicit held-page retry",902),runId,Ct);
        var shown=(await Store(restarted).ReadPreviewAsync(Scope,id,111,Ct)).ShouldNotBeNull();shown.Revision.ShouldBe(held.Revision+1);
        ReviewFixAssertAllPagesDelivered(shown,s.Telegram);
        s.Telegram.SentMessages.Count(m=>m.ChatId==Scope.ChatId&&m.TopicId==Scope.TopicId&&m.ReplyToMessageId==902
            &&m.Text==$"Запуск {runId:D}: {(comparison?"running":"preview")}; завершено окон 0/{(comparison?1:0)}; осталось входов 1.").ShouldBe(1);
        s.Telegram.SentMessages.Count.ShouldBe(2+shown.PageCount);
        (await restarted.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await restarted.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        if(comparison){(await Accept(restarted,shown)).Status.ShouldBe(VetMutationStatus.Applied);(await restarted.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);}
        s.Chat.RequestedMessages.ShouldBeEmpty();restarted.Chat.RequestedMessages.ShouldBeEmpty();(await restarted.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_marker_sql_failure_before_send_never_reaches_transport_or_fact_authority()
    {
        await SeedAsync();var fault=new FenceSqlFault("first-marker-failure");await using var s=Open(interceptor:fault);
        await Original(s,1);var r=await ReviewFixStageInitial(s);fault.Armed=true;
        (await FenceComposer(s).DeliverAsync(Scope,r,111,s.Telegram,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        fault.Hits.ShouldBe(1);s.Telegram.SentMessages.ShouldBeEmpty();await using var read=Open();
        var held=(await Store(read).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();held.State.ShouldBe("preview_failed");
        held.DeliveredPagesJson.ShouldBe("[]");held.CompletePreviewDelivered.ShouldBeFalse();
        (await read.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await read.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);(await read.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_marker_commit_response_loss_holds_without_a_transport_dispatch()
    {
        await SeedAsync();var lost=new FenceLostCommit();await using var s=Open(interceptor:lost);
        await Original(s,1);var r=await ReviewFixStageInitial(s);lost.Armed=true;
        (await FenceComposer(s).DeliverAsync(Scope,r,111,s.Telegram,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        lost.Hits.ShouldBe(1);s.Telegram.SentMessages.ShouldBeEmpty();await using var read=Open();
        var held=(await Store(read).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();held.State.ShouldBe("preview_failed");held.DeliveredPagesJson.ShouldBe("[]");
        await App(read).ResumeAsync(Bot,s.Telegram,Ct);s.Telegram.SentMessages.ShouldBeEmpty();
        held.Revision.ShouldBe(r.Revision);(await read.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await read.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_two_concurrent_default_deliveries_send_one_exact_page_and_complete_one_proof()
    {
        await SeedAsync();await using var setup=Open();await Original(setup,1);var r=await ReviewFixStageInitial(setup);r.PageCount.ShouldBe(1);
        await using var first=Open();await using var second=Open();var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport=new FenceTelegram(setup.Telegram,async(_,_)=>{entered.TrySetResult(true);await release.Task.WaitAsync(TimeSpan.FromSeconds(30));});
        var firstSend=FenceComposer(first).DeliverAsync(Scope,r,111,transport,null,Ct);
        try
        {await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
         (await FenceComposer(second).DeliverAsync(Scope,r,222,transport,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Stale);
         setup.Telegram.SentMessages.Count.ShouldBe(1);}
        finally{release.TrySetResult(true);await firstSend.WaitAsync(TimeSpan.FromSeconds(30));}
        (await firstSend).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var shown=(await Store(setup).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();ReviewFixAssertAllPagesDelivered(shown,setup.Telegram);
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(shown.DeliveredPagesJson,Json)!.Length.ShouldBe(1);transport.Delivered.Count.ShouldBe(1);
        shown.Revision.ShouldBe(r.Revision);(await setup.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await setup.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_known_recorded_page_is_merged_and_completed_without_sending_it_again()
    {
        await SeedAsync();await using var s=Open();await Original(s,1);var r=await ReviewFixStageInitial(s);r.PageCount.ShouldBe(1);
        var page=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!.Single();var id=await s.Telegram.SendTextAsync(Scope.ChatId,Scope.TopicId,page,null,Ct);
        var h=new VetPhotoReviewHandle(Scope,r.Id,r.Revision,r.OperationKey,111);
        (await Store(s).RecordPageDeliveryAsync(h,0,id,Hash(page),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).BeginPageDeliveryAsync(h,0,Hash(page),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        // The original in-memory review still has an empty proof list, so Composer must merge the store's known proof.
        r.DeliveredPagesJson.ShouldBe("[]");(await FenceComposer(s).DeliverAsync(Scope,r,111,s.Telegram,null,Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        s.Telegram.SentMessages.Count.ShouldBe(1);var shown=(await Store(s).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();
        ReviewFixAssertAllPagesDelivered(shown,s.Telegram);shown.AcceptancePromptMessageId.ShouldBe(id);(await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_claimed_page_only_accepts_exact_first_missing_completion_and_preserves_record_replay()
    {
        await SeedAsync();await using var s=Open();var batch=await FenceBatchPreview(s);var r=batch.Review;
        var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!;var h=new VetPhotoReviewHandle(Scope,r.Id,r.Revision,r.OperationKey,111);
        (await Store(s).BeginPageDeliveryAsync(h,1,Hash(pages[1]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Store(s).BeginPageDeliveryAsync(h,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var before=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking());
        (await Store(s).BeginPageDeliveryAsync(h,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(s).RecordPageDeliveryAsync(h,1,9001,Hash(pages[1]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Store(s).RecordPageDeliveryAsync(h,0,9000,new string('0',64),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Store(s).CompleteDeliveryAsync(h,9000,Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking())).ShouldBe(before);
        (await Store(s).RecordPageDeliveryAsync(h,0,9000,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var known=(await Store(s).ReadPreviewAsync(Scope,r.Id,111,Ct)).ShouldNotBeNull();known.State.ShouldBe("preview");
        (await Store(s).RecordPageDeliveryAsync(h,0,9000,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Store(s).BeginPageDeliveryAsync(h,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Store(s).BeginPageDeliveryAsync(h,2,Hash(pages[2]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(known.DeliveredPagesJson,Json)!.Single().ShouldBe(new VetPhotoPageDelivery(0,9000,Hash(pages[0])));
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("family")][InlineData("topic")][InlineData("bot")][InlineData("telegram")]
    [InlineData("actor")][InlineData("revision")][InlineData("operation")][InlineData("profile")]
    [InlineData("input")][InlineData("hash")][InlineData("page")][InlineData("callback")]
    public async Task PreviewFence_wrong_scope_current_revision_or_immutable_page_never_claims_or_writes_proof(string wrong)
    {
        await SeedAsync();await using var s=Open();var b=await FenceBatchPreview(s);var r=b.Review;var scope=Scope;
        if(wrong=="family")scope=scope with{FamilyId=FamilyId+777};if(wrong=="topic")scope=scope with{TopicId=8};
        if(wrong=="bot")scope=scope with{BotDbId=Bot.BotDbId+777};if(wrong=="telegram")scope=scope with{TelegramBotId=Bot.TelegramBotId+777};
        if(wrong=="profile")
        {var p=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);(await s.Profiles.UpdateAsync(FamilyId,Bot.BotDbId,111,p.Revision,[new("TimeZone","Europe/Berlin")],Ct)).Applied.ShouldBeTrue();}
        if(wrong=="input")await Original(s,1,edit:true);
        var h=new VetPhotoReviewHandle(scope,r.Id,wrong=="revision"?r.Revision+1:r.Revision,wrong=="operation"?Guid.NewGuid():r.OperationKey,
            wrong=="actor"?333:111,wrong=="callback"?9000:null);
        var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!;var before=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking());
        if(wrong is "family" or "bot" or "telegram")
        {var error=await Should.ThrowAsync<InvalidOperationException>(()=>Store(s).BeginPageDeliveryAsync(h,0,Hash(pages[0]),Ct));
         error.Message.ShouldBe(wrong=="family"?"Vet scope is not active.":"Vet bot scope is invalid.");}
        else
        {var result=await Store(s).BeginPageDeliveryAsync(h,wrong=="page"?-1:0,wrong=="hash"?new string('0',64):Hash(pages[0]),Ct);
         result.Status.ShouldBe(wrong is "actor" or "hash" or "page"?VetPhotoWorkflowStatus.Refused:VetPhotoWorkflowStatus.Stale);}
        (await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking())).ShouldBe(before);s.Telegram.SentMessages.ShouldBeEmpty();
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);(await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task PreviewFence_explicit_retry_advances_revision_and_fences_old_successful_send_proof()
    {
        await SeedAsync();await using var s=Open();var b=await FenceBatchPreview(s);var r=b.Review;var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!;
        var old=new VetPhotoReviewHandle(Scope,r.Id,r.Revision,r.OperationKey,111);
        (await Store(s).BeginPageDeliveryAsync(old,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var retry=(await Store(s).RetryPreviewAsync(old with{ActorUserId=222},Ct)).Review.ShouldNotBeNull();retry.Revision.ShouldBe(r.Revision+1);
        var before=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking());
        (await Store(s).RecordPageDeliveryAsync(old,0,9000,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking())).ShouldBe(before);
        var current=new VetPhotoReviewHandle(Scope,retry.Id,retry.Revision,retry.OperationKey,222);
        (await Store(s).BeginPageDeliveryAsync(current,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).RecordPageDeliveryAsync(current,0,9001,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var known=(await Store(s).ReadPreviewAsync(Scope,r.Id,222,Ct)).ShouldNotBeNull();known.State.ShouldBe("preview");
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(known.DeliveredPagesJson,Json)!.Single().MessageId.ShouldBe(9001);
        known.CompletePreviewDelivered.ShouldBeFalse();(await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);(await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("input")]
    [InlineData("profile")]
    public async Task PreviewFence_claimed_page_completion_rechecks_current_source_and_profile(string changed)
    {
        await SeedAsync();await using var s=Open();var b=await FenceBatchPreview(s);var r=b.Review;
        var pages=JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson,Json)!;
        var h=new VetPhotoReviewHandle(Scope,r.Id,r.Revision,r.OperationKey,111);
        (await Store(s).BeginPageDeliveryAsync(h,0,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if(changed=="input")await Original(s,1,edit:true);
        else
        {var profile=await s.Profiles.GetOrCreateAsync(FamilyId,Bot.BotDbId,Ct);
         (await s.Profiles.UpdateAsync(FamilyId,Bot.BotDbId,111,profile.Revision,[new("TimeZone","Europe/Berlin")],Ct)).Applied.ShouldBeTrue();}
        var before=await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking());
        (await Store(s).RecordPageDeliveryAsync(h,0,9000,Hash(pages[0]),Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking())).ShouldBe(before);
        var held=await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(x=>x.Id==r.Id,Ct);
        held.CompletePreviewDelivered.ShouldBeFalse();held.DeliveredPagesJson.ShouldBe("[]");held.AcceptancePromptMessageId.ShouldBeNull();
        s.Telegram.SentMessages.ShouldBeEmpty();(await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);(await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    private sealed class FenceSqlFault(string mode):DbCommandInterceptor
    {
        public bool Armed{get;set;} public int Hits{get;private set;} private bool knownPageRecorded;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(!Armed||!command.CommandText.Contains("vet_photo_reviews",StringComparison.OrdinalIgnoreCase))return ValueTask.FromResult(result);
            var update=command.CommandText.TrimStart().StartsWith("UPDATE",StringComparison.OrdinalIgnoreCase);
            var marker=update&&command.Parameters.Cast<DbParameter>().Any(p=>p.Value is string state&&state=="preview_failed");
            if(mode=="post-send-double"&&(update&&Hits==0||!update&&Hits==1))
            {Hits++;throw new IOException("synthetic scoped review persistence fault");}
            if(mode=="first-marker-failure"&&marker&&Hits==0)
            {Hits++;throw new IOException("synthetic marker rollback before transport");}
            if(mode=="second-marker-fatal")
            {
                if(update&&command.CommandText.Contains("delivered_pages_json",StringComparison.OrdinalIgnoreCase))knownPageRecorded=true;
                else if(!update&&knownPageRecorded&&Hits==0)
                {Hits++;throw new OutOfMemoryException("synthetic fatal interruption before next marker commit");}
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FenceLostCommit:DbTransactionInterceptor
    {
        public bool Armed{get;set;} public int Hits{get;private set;}
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        {if(Armed){Armed=false;Hits++;throw new IOException("synthetic committed marker response lost");}return Task.CompletedTask;}
    }

    private sealed class FenceTelegram(FakeTelegramClient inner, Func<int,string,Task>? afterSend = null) : ITelegramClient
    {
        public List<(int MessageId, string Text)> Delivered { get; } = [];
        public List<(long Chat, int Message, IReadOnlyList<InlineButton> Buttons)> EditedButtons { get; } = [];
        public int Sends { get; private set; }
        public async Task<int> SendTextAsync(long chat, int? topic, string text, int? reply, CancellationToken ct)
        {
            Sends++;
            var id = await inner.SendTextAsync(chat, topic, text, reply, ct); Delivered.Add((id, text));
            if (afterSend != null) await afterSend(id, text);
            return id;
        }
        public Task EditMessageButtonsAsync(long chat, int message, IReadOnlyList<InlineButton> buttons, CancellationToken ct)
        {
            EditedButtons.Add((chat, message, buttons)); return inner.EditMessageButtonsAsync(chat, message, buttons, ct);
        }
        public Task<long> DownloadFileAsync(string id, Stream destination, long max, CancellationToken ct) => inner.DownloadFileAsync(id, destination, max, ct);
        public Task<BotIdentity> GetMeAsync(CancellationToken ct) => inner.GetMeAsync(ct);
        public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeout, IReadOnlyList<UpdateKind> kinds, CancellationToken ct) => inner.GetUpdatesAsync(offset, timeout, kinds, ct);
        public Task SendChatActionAsync(long chat, int? topic, string action, CancellationToken ct) => inner.SendChatActionAsync(chat, topic, action, ct);
        public Task SetReactionAsync(long chat, int message, string? emoji, CancellationToken ct) => inner.SetReactionAsync(chat, message, emoji, ct);
        public Task<int> SendTextWithButtonsAsync(long chat, int? topic, string text, IReadOnlyList<InlineButton> buttons, int? reply, CancellationToken ct) => inner.SendTextWithButtonsAsync(chat, topic, text, buttons, reply, ct);
        public Task EditMessageTextAsync(long chat, int message, string text, CancellationToken ct) => inner.EditMessageTextAsync(chat, message, text, ct);
        public Task AnswerCallbackAsync(string id, string? text, CancellationToken ct) => inner.AnswerCallbackAsync(id, text, ct);
        public Task<string> GetManagedBotTokenAsync(long id, CancellationToken ct) => inner.GetManagedBotTokenAsync(id, ct);
    }

}
