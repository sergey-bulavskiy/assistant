using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet.Photos;

public sealed class VetPhotoRunApplication(IVetPhotoRunStore runs,IVetPhotoRunEvidenceStore evidence,
    IVetPhotoRunReviewSelectionStore replacement,IVetPhotoWorkflowStore workflow,
    IVetPhotoPresentationStore presentation,IVetProfileStore profiles,IVetPhotoDiaryStore diary,
    VetPhotoReviewComposer composer,IApprovalService approvals,ILogger<VetPhotoRunApplication> logger):IVetPhotoRunApplication
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Choice(Guid SourceId,Guid InputId,Guid ResultId,bool Restore);
    private async Task<bool> Authorized(ReceivingBot bot,IncomingMessage m,CancellationToken ct)=>
        bot.Role=="vet"&&bot.FamilyId is { } family&&m.UserId is { } actor
        &&await approvals.FindFamilyMemberStatusAsync(family,actor,ct)==FamilyMemberStatus.Approved
        &&(m.ChatType=="private"&&m.ChatId==actor&&m.TopicId==null
            ||await approvals.FindPlaceStatusAsync(bot.BotDbId,m.ChatId,m.TopicId,ct)==PlaceStatus.Approved);
    private static Task Say(ITelegramClient client,VetDiaryScope s,int? reply,string text,CancellationToken ct)=>
        client.SendTextAsync(s.ChatId,s.TopicId,text,reply,ct);
    private static VetPhotoReviewHandle Handle(VetDiaryScope s,VetPhotoReview r,long actor,int? prompt=null)=>new(s,r.Id,r.Revision,r.OperationKey,actor,prompt);
    public async Task BeginAsync(ReceivingBot bot,ITelegramClient client,IncomingMessage message,VetPhotoOperation op,Guid operationKey,CancellationToken ct)
    {
        if(!await Authorized(bot,message,ct))return;var scope=VetDiaryScope.From(bot,message);var actor=message.UserId!.Value;
        if(op.Kind is not ("reprocess" or "delete_originals"))return;
        var ids=op.SourceIds.ToHashSet();
        if(op.CandidateIds.Count+op.ItemNumbers.Count>0)
        {
            var batch=op.BatchId==null?null:await workflow.GetBatchAsync(scope,op.BatchId.Value,actor,ct);
            if(batch==null||op.CandidateIds.Any(id=>batch.Items.All(i=>i.Candidate.Id!=id))||op.ItemNumbers.Any(n=>batch.Items.All(i=>i.Source.ItemNumber!=n)))
            {await Say(client,scope,message.MessageId,"Нужна одна точная партия и принадлежащие ей источники.",ct);return;}
            foreach(var item in batch.Items.Where(i=>op.CandidateIds.Contains(i.Candidate.Id)||op.ItemNumbers.Contains(i.Source.ItemNumber!.Value)))ids.Add(item.Source.Id);
        }
        var mode=op.SelectionMode=="all_originals"?VetPhotoRunSelectionMode.AllOriginals:ids.Count>0?VetPhotoRunSelectionMode.Selected:VetPhotoRunSelectionMode.Current;
        if(op.SelectionMode=="selected"&&ids.Count==0||mode==VetPhotoRunSelectionMode.AllOriginals&&(ids.Count>0||op.BatchId!=null||op.FromDate!=null||op.UntilDate!=null))
        {await Say(client,scope,message.MessageId,"Выберите точные источники или все сохранённые версии в этом месте без дополнительных фильтров.",ct);return;}
        var refs=ids.Count==0?null:await evidence.ResolveCurrentReferencesAsync(scope,ids.Order().ToArray(),actor,ct);
        if(ids.Count>0&&refs==null){await Say(client,scope,message.MessageId,op.Kind=="reprocess"?"Выбранные оригиналы недоступны; для повторной обработки удалённых байтов загрузите фото заново. Новые вызовы не начаты.":"Выбранный оригинал недоступен; новые вызовы не начаты.",ct);return;}
        var profile=await profiles.GetOrCreateAsync(scope.FamilyId,scope.BotDbId,ct);
        DateOnly? ParseDate(string? value)=>value==null?null:DateOnly.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        var staged=await runs.StageRunAsync(new(scope,operationKey,actor,profile.Revision,op.Kind=="reprocess"?VetPhotoRunPurpose.Reprocess:VetPhotoRunPurpose.DeleteOriginals,
            mode,op.Kind=="reprocess"?"gpt-6.1-sol":"",op.Kind=="reprocess"?"codex-cli":"",op.BatchId,refs,ParseDate(op.FromDate),ParseDate(op.UntilDate),
            op.DateAxis=="upload"?VetPhotoRunDateAxis.Upload:VetPhotoRunDateAxis.Measurement),ct);
        if(staged.Run==null||staged.Review==null){await Say(client,scope,message.MessageId,op.Kind=="delete_originals"?"Удалять оригиналы может только владелец; выбранная область недоступна или устарела. Новые действия не начаты.":staged.SelectionCounts?.DeletedByteSources>0?"Выбранные оригиналы недоступны; для повторной обработки удалённых байтов загрузите фото заново. Новые вызовы не начаты.":"Область недоступна или устарела; новые вызовы не начаты.",ct);return;}
        await composer.DeliverAsync(scope,staged.Review,actor,client,message.MessageId,ct);
        await Say(client,scope,message.MessageId,$"Запуск {staged.Run.Id:D}. После подтверждения области каждое окно начинается явно: /photos_continue {staged.Run.Id:D}.",ct);
    }
    public async Task ContinueAsync(ReceivingBot bot,ITelegramClient client,IncomingMessage m,Guid runId,CancellationToken ct)
    {
        if(!await Authorized(bot,m,ct))return;var scope=VetDiaryScope.From(bot,m);var actor=m.UserId!.Value;
        var changed=await runs.ContinueRunAsync(new(scope,runId,actor),ct);
        if(changed.Review!=null)await composer.DeliverAsync(scope,changed.Review,actor,client,m.MessageId,ct);
        await Say(client,scope,m.MessageId,changed.Status is VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing
            ?$"Запуск {runId:D}: окно {changed.Window?.Ordinal+1} — {changed.Window?.State}. Новые факты требуют полного просмотра."
            :"Окно недоступно, занято или устарело; скрытого повтора нет.",ct);
    }
    public async Task ShowAsync(ReceivingBot bot,ITelegramClient client,IncomingMessage m,Guid runId,CancellationToken ct)
    {if(await Authorized(bot,m,ct))await Drive(new(VetDiaryScope.From(bot,m),runId,m.UserId!.Value),client,m.MessageId,true,ct);}
    public async Task CancelAsync(ReceivingBot bot,ITelegramClient client,IncomingMessage m,Guid runId,CancellationToken ct)
    {
        if(!await Authorized(bot,m,ct))return;var scope=VetDiaryScope.From(bot,m);
        var result=await runs.CancelRunAsync(new(scope,runId,m.UserId!.Value),ct);
        await Say(client,scope,m.MessageId,result.Run==null?"Запуск недоступен.":"Новые окна остановлены. Сохранённые факты, результаты и неопределённые расходы сохранены.",ct);
    }
    public async Task<bool> ConfirmAsync(VetDiaryScope scope,VetPhotoReview review,long actor,int? prompt,ITelegramClient client,int? reply,CancellationToken ct)
    {
        var owner=await evidence.FindReviewRunAsync(scope,review.Id,actor,ct);if(owner==null)return false;
        var handle=new VetPhotoRunHandle(scope,owner.RunId,actor);
        if(review.Kind is "reextract_selection" or "delete_originals_selection")
        {
            var approved=await runs.ApproveRunAsync(handle,Handle(scope,review,actor,prompt),ct);
            await Say(client,scope,reply,approved.Status is VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing
                ?$"Область подтверждена. Следующее окно: /photos_continue {owner.RunId:D}. Вызовы пока не начаты.":"Подтверждение устарело; нужен новый полный просмотр.",ct);return true;
        }
        if(review.Kind=="delete_originals"&&owner.WindowId is { } deletion)
        {var result=await runs.ConfirmDeletionWindowAsync(handle,deletion,Handle(scope,review,actor,prompt),ct);await Say(client,scope,reply,$"Удаление оригиналов: {result.Status}. Факты и происхождение сохранены.",ct);return true;}
        if(review.Kind is not ("reextract_comparison" or "correction")||owner.WindowId==null)return false;
        var applied=await diary.ApplyPhotoReviewAsync(new(scope,review.Id,review.Revision,review.OperationKey,actor,prompt),ct);
        if(review.Kind=="reextract_comparison"&&applied.Status is VetMutationStatus.Applied or VetMutationStatus.NoChange or VetMutationStatus.AlreadyApplied)
            await runs.ReconcileWindowAsync(handle,owner.WindowId.Value,ct);
        await Say(client,scope,reply,$"Показанный набор: {applied.Status}. Следующее окно начинается только явно.",ct);return true;
    }
    public async Task ResumeAsync(ReceivingBot bot,ITelegramClient client,CancellationToken ct)
    {
        if(bot.Role!="vet"||bot.FamilyId is not { } family)return;
        foreach(var run in await evidence.GetRecoverableRunsAsync(family,bot.BotDbId,5,ct))
            await Drive(new(run.Scope,run.RunId,run.ActorUserId),client,null,false,ct);
    }
    public async Task WorkFinishedAsync(ReceivingBot bot,ITelegramClient client,VetPhotoWork work,VetPhotoProcessResult result,CancellationToken ct)
    {
        if(work.ScheduledAttemptKey is not { } attempt||work.Scope.FamilyId!=bot.FamilyId||work.Scope.BotDbId!=bot.BotDbId||work.Scope.TelegramBotId!=bot.TelegramBotId)return;
        var run=await evidence.FindAttemptRunAsync(work.Scope,attempt,work.ActorUserId,ct);if(run!=null)await Drive(new(run.Scope,run.RunId,run.ActorUserId),client,null,false,ct);
    }
    private async Task Drive(VetPhotoRunHandle handle,ITelegramClient client,int? reply,bool interactive,CancellationToken ct)
    {
        var page=await runs.GetRunAsync(handle,0,5,ct);
        if(page==null){if(interactive)await Say(client,handle.Scope,reply,"Запуск недоступен или приостановлен изменением профиля/области. Скрытого повтора нет.",ct);else logger.LogWarning("Photo run recovery held: stale scope or profile");return;}
        if(interactive)await Say(client,handle.Scope,reply,$"Запуск {page.Run.Id:D}: {page.Run.State}; завершено окон {page.CompletedWindows}/{page.TotalWindows}; осталось входов {page.RemainingInputs}.",ct);
        if(page.Run.State=="stale")
        {if(interactive)await Say(client,handle.Scope,reply,"Запуск удержан: прежнее подтверждение устарело. Окна, результаты и возможный расход сохранены. Новых вызовов нет; выберите точный сохранённый /photos_result либо создайте новый полный выбор.",ct);return;}
        if(page.Run.State=="preview")
        {var initial=await presentation.ReadPreviewAsync(handle.Scope,page.Run.SelectionReviewId,handle.ActorUserId,ct);if(initial!=null&&!initial.CompletePreviewDelivered)await composer.DeliverAsync(handle.Scope,initial,handle.ActorUserId,client,reply,ct,explicitRetry:interactive);return;}
        if(page.Run.State is "cancelled" or "completed"||page.Run.CancelledAt!=null)return;
        var ordinal=page.Run.NextWindowOrdinal;
        if(ordinal>=5)page=await runs.GetRunAsync(handle,ordinal,5,ct)??page;
        var window=page.Windows.SingleOrDefault(w=>w.Ordinal==ordinal);if(window==null)return;
        if(window.ComparisonReviewId is { } reviewId)
        {
            var review=await presentation.ReadPreviewAsync(handle.Scope,reviewId,handle.ActorUserId,ct);if(review==null)return;
            if(review.State=="accepted")await runs.ReconcileWindowAsync(handle,window.Id,ct);
            else if(review.State is "preview" or "preview_failed"&&!review.CompletePreviewDelivered)await composer.DeliverAsync(handle.Scope,review,handle.ActorUserId,client,reply,ct,explicitRetry:interactive);
            else if(review.State=="stale"&&interactive)await Say(client,handle.Scope,reply,"Сохранённый просмотр устарел. Для нового просмотра без вызова выберите точный /photos_result; данные не применены.",ct);
            return;
        }
        if(window.State is not ("queued" or "running"))return;
        var read=await evidence.ReadWindowAsync(handle,window.Id,ct);
        if(read==null){if(interactive)await Say(client,handle.Scope,reply,"Выбранные доказательства устарели; окно удержано без новых вызовов.",ct);return;}
        if(!Terminal(read))
        {if(interactive)await Say(client,handle.Scope,reply,$"Окно {window.Ordinal+1}: возвращено {read.Items.Count(i=>i.Attempt?.State=="returned")}/{read.Items.Count}; активные попытки ожидаются без повтора.",ct);return;}
        await Compose(handle,read,null,client,reply,ct,interactive);
    }
    private static bool Terminal(VetPhotoRunEvidence r)=>r.Items.All(i=>i.Attempt is { } a&&
        (a.State=="returned"&&!a.ReservedResultSlot&&i.Evidence.Extraction!=null||a.State=="failed"&&!a.ReservedResultSlot
            ||a.State=="unknown"&&a.ReservedResultSlot&&a.ExtractionResultId==null));
    public async Task SelectResultAsync(ReceivingBot bot,ITelegramClient client,IncomingMessage m,Guid runId,Guid windowId,Guid sourceId,Guid inputId,Guid resultId,bool restoreRequested,CancellationToken ct)
    {
        if(!await Authorized(bot,m,ct))return;var h=new VetPhotoRunHandle(VetDiaryScope.From(bot,m),runId,m.UserId!.Value);
        var read=await evidence.ReadWindowAsync(h,windowId,ct);
        var selected=read?.Items.SingleOrDefault(i=>i.Snapshot.SourceId==sourceId&&i.Snapshot.InputRevisionId==inputId&&i.Evidence.Extraction?.Id==resultId);
        if(read==null||selected==null||selected.Attempt?.State!="returned"||selected.Evidence.Extraction?.State!="comparison"
            ||read.Window.State is not ("queued" or "running" or "awaiting_review" or "completed" or "cancelled"))
        {await Say(client,h.Scope,m.MessageId,"Нужны точные выбранные источник, вход и сохранённый результат этого окна.",ct);return;}
        if(read.Run.State!="stale"&&read.Window.State is not ("completed" or "cancelled")&&!Terminal(read))
        {await Say(client,h.Scope,m.MessageId,"Активные попытки ещё не завершены; результат не применяется и не повторяется.",ct);return;}
        await Compose(h,read,new(sourceId,inputId,resultId,restoreRequested),client,m.MessageId,ct,true);
    }
    private async Task Compose(VetPhotoRunHandle h,VetPhotoRunEvidence read,Choice? choice,ITelegramClient client,int? reply,CancellationToken ct,bool explicitRetry)
    {
        var late=read.Run.State=="stale"||read.Window.State is "completed" or "cancelled";if(late&&choice==null)return;
        if(!late&&(read.Run.State!="running"||read.Run.CancelledAt!=null||read.Run.NextWindowOrdinal!=read.Window.Ordinal))return;
        var profile=await profiles.GetOrCreateAsync(h.Scope.FamilyId,h.Scope.BotDbId,ct);
        foreach(var id in read.Items.Where(i=>!late||i.Snapshot.SourceId==choice!.SourceId).Select(i=>i.Evidence.Source.BatchId).Distinct())
        {if(id==null)return;var b=await workflow.GetBatchAsync(h.Scope,id.Value,h.ActorUserId,ct);if(b==null)return;
         var refresh=await presentation.RefreshProfileSnapshotAsync(h.Scope,id.Value,b.Batch.ReviewRevision,profile.Revision,h.ActorUserId,ct);if(refresh.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))return;}
        var reread=await evidence.ReadWindowAsync(h,read.Window.Id,ct);if(reread==null)return;read=reread;
        var blocks=new List<string>();var selection=new List<VetPhotoDiarySelection>();
        foreach(var item in read.Items)
        {
            var e=item.Evidence;var parsed=e.Extraction==null?null:VetPhotoInterpretationParser.Parse(e.Extraction.StructuredJson,e.Source.Id,e.Input.Id);
            blocks.Add($"Источник {e.Source.Id:D}; вход {e.Input.Id:D}, версия {e.Input.Ordinal} ({(e.Input.Id==e.Source.CurrentInputRevisionId?"текущая":"прежняя")}); попытка {item.Snapshot.AttemptKey:D}: {item.Attempt?.State??"не начата"}; результат {e.Extraction?.Id.ToString("D")??"нет"}. "
                +(item.Attempt?.State=="unknown"?"Исход неизвестен, резерв результата и возможный расход сохранены. Нового вызова нет.":""));
            blocks.AddRange(VetPhotoReviewEvidence.Data("Неизменяемая подпись выбранного входа (данные)",e.Input.Caption));
            blocks.AddRange(VetPhotoReviewEvidence.Image("Выбранный новый дисплей (данные)",e));
        }
        foreach(var group in read.Items.GroupBy(i=>i.Snapshot.SourceId))
        {
            var chosen=choice?.SourceId==group.Key?group.Single(i=>i.Snapshot.InputRevisionId==choice.InputId&&i.Evidence.Extraction?.Id==choice.ResultId)
                :group.FirstOrDefault(i=>i.Snapshot.InputRevisionId==i.Evidence.Source.CurrentInputRevisionId)??(group.Count()==1?group.Single():group.First());
            var explicitChoice=choice?.SourceId==group.Key;
            if(late&&!explicitChoice)continue;
            var e=chosen.Evidence;var b=(await workflow.GetBatchAsync(h.Scope,e.Source.BatchId!.Value,h.ActorUserId,ct))!.Batch;
            var assumptions=JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(b.AssumptionsJson,Json)!;
            blocks.Add(VetPhotoReviewEvidence.Assumptions(assumptions));
            blocks.AddRange(VetPhotoReviewEvidence.Candidate(e.Candidate));
            blocks.AddRange(VetPhotoReviewEvidence.Data("Текущий сохранённый факт",
                VetPhotoReviewEvidence.StateText(e.OwnedEvent==null?"Факт отсутствует":$"Факт #{e.OwnedEvent.Id}, версия {e.OwnedEvent.Revision}",State(e.OwnedEvent))));
            if(e.Candidate.InputRevisionId is { } oldInput&&e.Candidate.ExtractionResultId is { } oldResult
                &&(oldInput!=e.Input.Id||oldResult!=e.Extraction?.Id))
            {
                var old=await presentation.ReadEvidenceAsync(h.Scope,e.Source.Id,oldInput,oldResult,h.ActorUserId,ct);
                if(old?.Extraction?.Id!=oldResult)return;
                blocks.AddRange(VetPhotoReviewEvidence.Data("Неизменяемая подпись прежнего основания (данные)",old.Input.Caption));
                blocks.AddRange(VetPhotoReviewEvidence.Image("Прежний дисплей кандидата (данные)",old));
            }
            var (context,captionFailure)=VetPhotoCaptionContext.Read(e.Caption);
            var image=e.Extraction==null?null:VetPhotoInterpretationParser.Parse(e.Extraction.StructuredJson,e.Source.Id,e.Input.Id);
            var validation=image==null?new VetPhotoValidation(null,["image_result_unavailable"]):VetPhotoValidationRules.Validate(image,context,assumptions,e.Input.ReceivedAt);
            if(captionFailure is not (null or "caption_not_saved"))validation=new(null,[captionFailure]);
            blocks.AddRange(VetPhotoReviewEvidence.Data("Эффективное предложение выбранного результата",VetPhotoReviewEvidence.EffectiveText(validation.Effective)));
            blocks.AddRange(VetPhotoReviewEvidence.Data("Основание выбранного предложения (данные)",VetPhotoReviewEvidence.Context(context)));
            var protectedState=e.Candidate.RequiresExplicitRestoration||e.Candidate.State is "excluded" or "cancelled" or "deleted";
            var chooseAllowed=explicitChoice||group.Any(i=>i.Snapshot.InputRevisionId==e.Source.CurrentInputRevisionId)||group.Count()==1;
            var change=validation.Effective!=null&&e.Extraction?.State=="comparison"&&chosen.Attempt?.State=="returned"&&chooseAllowed
                &&(!protectedState||explicitChoice&&choice!.Restore)&&(!e.Candidate.ManuallyCorrected||explicitChoice)
                &&e.Candidate.State!="linked";
            if(!explicitChoice&&e.OwnedEvent is {DeletedAt:null} saved&&validation.Effective is { } proposed
                &&saved.Value==proposed.Value&&saved.Unit==proposed.Unit&&saved.OccurredAt==proposed.OccurredAt)change=false;
            var state=change?Fact(e,b.Id,validation.Effective!):State(e.OwnedEvent);
            var proof=await diary.GetPhotoCollisionProofAsync(h.Scope,profile.Id,e.Candidate.Id,state,ct);if(proof==null)return;
            if(change&&proof.HasCollisions&&e.Candidate.DuplicateDecision!="separate")
            {change=false;state=State(e.OwnedEvent);proof=await diary.GetPhotoCollisionProofAsync(h.Scope,profile.Id,e.Candidate.Id,state,ct);if(proof==null)return;}
            var disposition=change?(e.Candidate.EventId==null?"save":"correct"):"keep";
            blocks.Add($"Источник {e.Source.Id:D}: данные для выбранного набора. "
                +(chooseAllowed?"":"Несколько прежних результатов: выберите один точный /photos_result.")
                +(protectedState&&!change?" Требуется явное восстановление; текущее состояние сохранено.":"")
                +(captionFailure=="caption_not_saved"?" TEXT-подпись не сохранена; инсулин здесь не подтверждается.":"")
                +string.Join("; ",validation.Reasons));
            if(late&&!change){await Say(client,h.Scope,reply,"Выбранный результат требует уточнения, решения о повторе или явного восстановления; новый факт не предлагается.",ct);return;}
            selection.Add(new(profile.Id,profile.Revision,b.Id,b.ReviewRevision,e.Candidate.Id,e.Candidate.Revision,e.Source.Id,e.Source.CurrentInputRevisionId,
                e.Source.CurrentOrdinal,e.Input.Id,e.Extraction?.Id,e.Candidate.ExtractionResultId,e.Original?.Id,e.Original?.Revision,e.Original?.State,
                e.Candidate.EventId,e.Candidate.EventRevision,disposition,change?e.Candidate.DuplicateDecision:"unresolved",null,null,null,
                explicitChoice&&choice!.Restore,context,state,proof));
        }
        foreach(var duplicate in selection.Where(s=>s.Disposition!="keep"&&s.State!=null)
            .GroupBy(s=>(s.State!.Value,s.State.Unit,s.State.OccurredAt)).Where(g=>g.Count()>1&&g.Any(s=>s.DuplicateDecision!="separate")))
        {
            foreach(var item in duplicate.ToArray())
            {
                var own=read.Items.Single(i=>i.Snapshot.SourceId==item.SourceId&&i.Snapshot.InputRevisionId==item.InputRevisionId).Evidence;
                var unchanged=State(own.OwnedEvent);var proof=await diary.GetPhotoCollisionProofAsync(h.Scope,profile.Id,item.CandidateId,unchanged,ct);if(proof==null)return;
                selection[selection.IndexOf(item)]=item with{Disposition="keep",State=unchanged,DuplicateDecision="unresolved",CollisionProof=proof};
                blocks.Add($"Источник {item.SourceId:D}: возможный повтор другого выбранного измерения; требуется явное решение, состояние пока сохранено.");
            }
        }
        foreach(var item in selection)
        {
            blocks.Add($"Окончательный выбранный набор: источник {item.SourceId:D}, вход {item.InputRevisionId:D}: {item.Disposition}; явное восстановление {item.ExplicitRestoration}.");
            blocks.AddRange(VetPhotoReviewEvidence.Data("Окончательное состояние показанного набора",VetPhotoReviewEvidence.StateText("После подтверждения",item.State)));
        }
        if(selection.Count==0)return;
        var preview=VetPhotoReviewFormatter.FormatBlocks($"Запуск {read.Run.Id:D}, окно {read.Window.Id:D}: {read.Items.Count} выбранных входов; изменения только явно показанных источников.",blocks,
            "Неизменённые/неопределённые строки сохраняют состояние и расходы. Подтверждение применяет только показанный набор; следующего вызова не запускает.");
        if(!preview.Success){await Say(client,h.Scope,reply,"Полный просмотр не помещается; набор удержан без применения. Выберите меньшую точную область.",ct);return;}
        var json=JsonSerializer.Serialize(selection,Json);var kind=late?VetPhotoReviewKind.Correction:VetPhotoReviewKind.ReextractComparison;
        var operation=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(read.Window.Id+":"+h.ActorUserId+":"+json+":"+string.Join("\n",preview.Pages))).AsSpan(0,16));
        var staged=await workflow.StageReviewAsync(new(h.Scope,operation,h.ActorUserId,kind,null,null,profile.Revision,json,preview,read.Window.Id),ct);
        if(staged.Review==null)return;
        if(!late)
        {
            VetPhotoRunChange attached;
            if(read.Window.ComparisonReviewId is { } oldId&&oldId!=staged.Review.Id)
            {var old=await presentation.ReadPreviewAsync(h.Scope,oldId,h.ActorUserId,ct);if(old==null)return;
             attached=await replacement.ReplaceComparisonAsync(h,read.Window.Id,old.Id,old.Revision,staged.Review.Id,ct);}
            else attached=await runs.AttachComparisonAsync(h,read.Window.Id,staged.Review.Id,ct);
            if(attached.Status is not (VetPhotoWorkflowStatus.Applied or VetPhotoWorkflowStatus.Existing))
            {await Say(client,h.Scope,reply,"Сравнение уже изменено или подтверждено; нужен свежий просмотр.",ct);return;}
        }
        if(!staged.Review.CompletePreviewDelivered)await composer.DeliverAsync(h.Scope,staged.Review,h.ActorUserId,client,reply,ct,explicitRetry:explicitRetry);
    }
    private static VetEventState Fact(VetPhotoPresentationEvidence e,Guid batch,VetPhotoEffectiveReading r)=>
        new("glucose",r.Value,r.Unit,null,r.OccurredAt,r.LocalTime,r.TimeZoneSnapshot,r.TimeEvidence,VetPhotoValidationRules.ValueUnitEvidence(r),"photo",e.Source.Id,0,
            null,e.Source.Id,batch,e.Input.Id,e.Extraction!.Id,e.Source.SourceAuthorUserId,e.Source.SourceMessageDbId!.Value,e.Source.TelegramMessageId);
    private static VetEventState? State(VetEvent? e)=>e==null?null:new(e.EventType,e.Value,e.Unit,e.Product,e.OccurredAt,e.LocalTime,e.TimeZoneSnapshot,e.OccurredAtSource,
        e.ValueUnitSource,e.SourceKind,e.SourceId,e.CandidateOrdinal,e.TextSourceId,e.PhotoSourceId,e.PhotoBatchId,e.InputRevisionId,e.ExtractionResultId,e.SourceAuthorUserId,
        e.SourceMessageDbId,e.TelegramMessageId,e.DeletedAt,e.DeleteReason,e.DeletedByUserId);
}
