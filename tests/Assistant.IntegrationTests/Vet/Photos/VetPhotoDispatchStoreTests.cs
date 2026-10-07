using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoDispatchStoreTests : VetTestBase
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly VetPhotoImageDecoder Decoder = new();
    private static readonly byte[] Original = ImageBytes();
    private VetPhotoStore Store(VetTestSession s) => new(s.Context, s.Current, Clock, new(), Decoder);
    private static VetPhotoAttachment Attachment(int id, long? size = null) =>
        new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", size ?? Original.Length, 32, 24);
    private static IncomingMessage ImageMessage(int id, VetDiaryScope scope, string caption = "synthetic caption", long actor = 111) =>
        Text(caption, id, actor) with { Kind = MessageKind.Photo, ChatId = scope.ChatId, TopicId = scope.TopicId,
            ChatType = scope.ChatId > 0 ? "private" : "supergroup" };
    private async Task<VetPhotoAdmission> PrepareAsync(VetTestSession s, int id = 1, string? textState = null,
        bool bind = true, bool archive = false, long? size = null, VetDiaryScope? selectedScope = null, long actor = 111)
    {
        var scope = selectedScope ?? Scope; var message = ImageMessage(id, scope, actor: actor);
        Guid? textId = null;
        if (textState != null)
        {
            var caption = await s.Diary.AdmitAsync(scope, message, id, Ct); textId = caption.Revision.Id;
            await s.Context.Set<VetTextSourceRevision>().Where(t => t.Id == textId)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.State, textState), Ct);
        }
        var admitted = await Store(s).AdmitAsync(scope, message, id, Attachment(id, size), textId, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        if (bind)
        {
            var stored = await s.Messages.StoreAsync(scope.TelegramBotId, id, message, Ct);
            (await Store(s).BindMessageAsync(scope, admitted.Source!.Id, stored.MessageDbId.ShouldNotBeNull(), Ct)).ShouldBeTrue();
        }
        if (archive)
        {
            var claim = (await Store(s).ReserveDownloadAsync(scope, admitted.Source!.Id, admitted.Input!.Id, actor, Ct)).Claim.ShouldNotBeNull();
            (await Store(s).CommitOriginalAsync(new(scope, actor, claim.Attempt.Id, claim.ClaimToken,
                admitted.Input.Id, Original, Decoder.Decode(Original, Ct).Image.ShouldNotBeNull()), Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        }
        return (await Store(s).GetSourceAsync(scope, admitted.Source!.Id, Ct)).ShouldNotBeNull();
    }
    private static string ResultJson(Guid source, Guid input) => JsonSerializer.Serialize(new
    {
        schema_version = 1, photo_source_id = source.ToString("D"), input_revision_id = input.ToString("D"), kind = "meter",
        displays = new[] { new { value_text = "5.6", decimal_value = 5.6m, unit = "mmol/L", year = 2031,
            year_displayed = true, month = 5, day = 11, time = "10:20", offset = "+00:00" } },
        reasons = Array.Empty<string>(), notes = (string?)null
    }, Json);
    private async Task<VetPhotoImageResult> CompleteAsync(VetTestSession s, VetPhotoAdmission admission, string version = "photo-v1")
    {
        var claim = (await Store(s).ClaimCurrentImageAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        return await Store(s).CompleteImageAsync(new(Scope, claim.AttemptKey, claim.ClaimToken, 111, claim.SourceId,
            claim.InputRevisionId, "gpt-6.1-sol", ResultJson(claim.SourceId, claim.InputRevisionId)) { PromptVersion = version }, Ct);
    }

    [Theory]
    [InlineData("admitted", false)] [InlineData("dispatching", false)] [InlineData("ready", false)]
    [InlineData("written", true)] [InlineData("completed", true)] [InlineData("failed", true)] [InlineData("paused", true)]
    public async Task Only_applied_or_terminal_caption_work_releases_image_queue(string textState, bool eligible)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, textState: textState);
        var due = await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        due.Count.ShouldBe(eligible ? 1 : 0);
        var input = await Store(s).ReadInputAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct);
        if (eligible)
        {
            due.Single().ShouldBe(new(Scope, admission.Source.Id, admission.Input.Id, 111));
            input!.Caption.ShouldBe("synthetic caption"); input.TextInputRevisionId.ShouldBe(admission.Input.TextInputRevisionId);
        }
        else input.ShouldBeNull();
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetTextSourceRevision>().SingleAsync()).State.ShouldBe(textState);
    }

    [Fact]
    public async Task No_caption_and_approved_private_source_are_eligible_without_an_approved_group_place()
    {
        await SeedAsync(); await using var s = Open(); var scope = Scope with { ChatId = 111, TopicId = null };
        var admission = await PrepareAsync(s, selectedScope: scope);
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBe([new(scope, admission.Source!.Id, admission.Input!.Id, 111)]);
        (await Store(s).ReadInputAsync(scope, admission.Source.Id, admission.Input.Id, 222, Ct)).ShouldBeNull();
        (await Store(s).ReadInputAsync(scope, admission.Source.Id, admission.Input.Id, 111, Ct))!.Id.ShouldBe(admission.Input.Id);
    }

    [Theory]
    [InlineData("bot")] [InlineData("member")] [InlineData("place")] [InlineData("unbound")]
    [InlineData("source_slot")] [InlineData("batch_cancelled")] [InlineData("batch_completed")]
    [InlineData("input_topic")] [InlineData("attempt_topic")]
    public async Task Current_queue_requires_active_exact_scopes_authorization_and_open_batch(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, bind: invalid != "unbound");
        if (invalid == "bot") await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled));
        if (invalid == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        if (invalid == "place") await s.Context.Places.Where(p => p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled));
        if (invalid == "source_slot") await s.Context.Set<VetPhotoSource>().ExecuteUpdateAsync(u => u.SetProperty(v => v.SourceSlot, 2));
        if (invalid.StartsWith("batch_", StringComparison.Ordinal)) await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(v => v.State, invalid == "batch_cancelled" ? "cancelled" : "completed"));
        if (invalid == "input_topic") await s.Context.Set<VetPhotoInputRevision>().ExecuteUpdateAsync(u => u.SetProperty(v => v.TopicId, 8));
        if (invalid == "attempt_topic") await s.Context.Set<VetPhotoAttempt>().ExecuteUpdateAsync(u => u.SetProperty(v => v.TopicId, 8));
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
    }

    [Theory]
    [InlineData("topic")] [InlineData("chat")] [InlineData("telegram")] [InlineData("author")]
    [InlineData("message")] [InlineData("chat_type")] [InlineData("sent")] [InlineData("direction")] [InlineData("kind")]
    public async Task Stored_transport_binding_is_exact_for_due_and_direct_input_reads(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s);
        var messages = s.Context.Messages.Where(m => m.Id == admission.Source!.SourceMessageDbId);
        switch (invalid)
        {
            case "topic": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.TopicId, 8)); break;
            case "chat": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.ChatId, -200)); break;
            case "telegram": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.BotId, 2001)); break;
            case "author": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.UserId, 222)); break;
            case "message": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.TelegramMessageId, 900)); break;
            case "chat_type": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.ChatType, "private")); break;
            case "sent": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.SentAt, Now.AddMinutes(1))); break;
            case "direction": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.Direction, MessageDirection.Out)); break;
            case "kind": await messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.Kind, MessageKind.Text)); break;
        }
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await Store(s).ReadInputAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct)).ShouldBeNull();
        (await Store(s).ReadStoredImageAsync(Scope, admission.Source.Id, admission.Input.Id, null, 111, Ct)).ShouldBeNull();
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
    }

    [Fact]
    public async Task Bounded_ordered_queue_and_input_reads_never_project_archived_bytes()
    {
        await SeedAsync(); var expected = new List<Guid>();
        await using (var seed = Open()) for (var i = 1; i <= 7; i++)
        { Clock.UtcNow = Now.AddSeconds(i); expected.Add((await PrepareAsync(seed, i, archive: true)).Source!.Id); }
        var observer = new ObserveSql(); await using var s = Open(interceptor: observer);
        var due = await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        due.Select(w => w.SourceId).ShouldBe(expected.Take(5)); due.ShouldAllBe(w => w.Scope == Scope && w.ActorUserId == 111 && w.ScheduledAttemptKey == null);
        (await Store(s).ReadInputAsync(Scope, due[0].SourceId, due[0].InputRevisionId, 111, Ct))!.Caption.ShouldBe("synthetic caption");
        observer.Commands.Count.ShouldBeGreaterThan(0);
        observer.Commands.ShouldAllBe(sql => !Regex.Match(sql, @"^\s*SELECT\s+(.*?)\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Groups[1].Value.Contains("\"content\"", StringComparison.OrdinalIgnoreCase));
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(0);
    }

    [Theory]
    [InlineData(0)] [InlineData(6)]
    public async Task Queue_limit_outside_one_to_five_is_rejected_without_claiming(int limit)
    {
        await SeedAsync(); await using var s = Open(); await PrepareAsync(s);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Store(s).GetDueAsync(FamilyId, Bot.BotDbId, limit, Ct));
        error.Message.ShouldBe("Photo work limit is invalid."); (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
    }

    [Theory]
    [InlineData("claimed_live", false)] [InlineData("claimed_expired", true)]
    [InlineData("dispatched_live", false)] [InlineData("dispatched_expired", true)]
    [InlineData("unknown", false)] [InlineData("failed", false)]
    public async Task Durable_image_state_only_schedules_unstarted_expiry_or_unknown_reconciliation(string state, bool eligible)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        var claim = (await Store(s).ClaimCurrentImageAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        var actual = state.StartsWith("claimed", StringComparison.Ordinal) ? "claimed" : state.StartsWith("dispatched", StringComparison.Ordinal) ? "dispatched" : state;
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == claim.AttemptKey).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, actual)
            .SetProperty(a => a.LeaseUntil, state.EndsWith("expired", StringComparison.Ordinal) ? Clock.UtcNow : Clock.UtcNow.AddMinutes(5)));
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).Count.ShouldBe(eligible ? 1 : 0);
        var persisted = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == claim.AttemptKey);
        persisted.State.ShouldBe(actual); persisted.ReservedResultSlot.ShouldBeTrue(); // Queue never releases unknown accounting.
    }

    [Fact]
    public async Task Known_download_timeout_has_exact_five_second_wait_and_maximum_two_downloads()
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s); var store = Store(s);
        var first = (await store.ReserveDownloadAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await store.RecordDownloadFailureAsync(Scope, first.Attempt.Id, first.ClaimToken, "download_timeout", true, Ct)).ShouldBeTrue();
        (await store.GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        Clock.UtcNow = Now.AddSeconds(4); (await store.GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        Clock.UtcNow = Now.AddSeconds(5); (await store.GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).Count.ShouldBe(1);
        var second = (await store.ReserveDownloadAsync(Scope, admission.Source.Id, admission.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        second.Attempt.Id.ShouldBe(first.Attempt.Id); second.Attempt.DownloadAttemptCount.ShouldBe(2);
        (await store.RecordDownloadFailureAsync(Scope, second.Attempt.Id, second.ClaimToken, "download_timeout", true, Ct)).ShouldBeTrue();
        Clock.UtcNow = Now.AddHours(1); (await store.GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var persisted = await s.Context.Set<VetPhotoAttempt>().SingleAsync(); persisted.State.ShouldBe("failed");
        persisted.DownloadAttemptCount.ShouldBe(2); persisted.ReservedBytes.ShouldBe(0); persisted.ReservedInputSlot.ShouldBeFalse();
    }

    [Theory]
    [InlineData("oversized")] [InlineData("superseded")] [InlineData("exhausted")]
    public async Task Terminal_or_expired_download_repair_releases_only_its_owned_reservation(string problem)
    {
        await SeedAsync(); await using var s = Open();
        var admission = await PrepareAsync(s, size: problem == "oversized" ? 10_485_761 : null);
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync();
        if (problem != "oversized")
        {
            var claim = (await Store(s).ReserveDownloadAsync(Scope, admission.Source!.Id, admission.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
            attempt = claim.Attempt;
            if (problem == "exhausted") await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == attempt.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.DownloadAttemptCount, 2));
            else await CaptionEditAsync(s, admission, archive: false);
            Clock.UtcNow = Now.AddMinutes(2);
        }
        await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        var repaired = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        repaired.State.ShouldBe(problem == "superseded" ? "retry_wait" : "failed");
        repaired.FailureCategory.ShouldBe(problem == "oversized" ? "encoded_image_too_large" : "download_timeout");
        repaired.DownloadAttemptCount.ShouldBe(problem == "oversized" ? 0 : problem == "exhausted" ? 2 : 1);
        repaired.RetryNotBefore.ShouldBe(problem == "superseded" ? Clock.UtcNow : (DateTimeOffset?)null);
        repaired.ReservedBytes.ShouldBe(0); repaired.ReservedInputSlot.ShouldBeFalse(); repaired.ClaimToken.ShouldBeNull(); repaired.LeaseUntil.ShouldBeNull();
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Repair_is_bounded_at_five_and_never_releases_live_download_or_unknown_image_slots()
    {
        await SeedAsync(); var oversized = new List<Guid>(); VetPhotoAdmission live, image;
        await using (var seed = Open())
        {
            for (var i = 1; i <= 7; i++) { Clock.UtcNow = Now.AddSeconds(i); oversized.Add((await PrepareAsync(seed, i, size: 10_485_761)).Input!.Id); }
            live = await PrepareAsync(seed, 20); var download = (await Store(seed).ReserveDownloadAsync(Scope, live.Source!.Id, live.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
            image = await PrepareAsync(seed, 21, archive: true);
            var claim = (await Store(seed).ClaimCurrentImageAsync(Scope, image.Source!.Id, image.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
            (await Store(seed).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
            (await Store(seed).RecordImageFailureAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, "outcome_unknown", VetPhotoImageFailureDisposition.OutcomeUnknown, Ct)).ShouldBeTrue();
            download.Attempt.ReservedBytes.ShouldBe(Original.Length);
        }
        await using var s = Open(); (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => oversized.Contains(a.InputRevisionId) && a.State == "failed")).ShouldBe(5);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => oversized.Contains(a.InputRevisionId) && a.State == "queued")).ShouldBe(2);
        var liveAttempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.InputRevisionId == live.Input!.Id);
        liveAttempt.State.ShouldBe("downloading"); liveAttempt.ReservedBytes.ShouldBe(Original.Length); liveAttempt.ReservedInputSlot.ShouldBeTrue();
        var unknown = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.InputRevisionId == image.Input!.Id && a.Kind == "image");
        unknown.State.ShouldBe("unknown"); unknown.ReservedResultSlot.ShouldBeTrue();
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => oversized.Contains(a.InputRevisionId) && a.State == "failed")).ShouldBe(7);
    }

    [Fact]
    public async Task Repair_transaction_rollback_preserves_reservation_and_same_context_retry_is_clean()
    {
        await SeedAsync(); VetPhotoAdmission admission;
        await using (var seed = Open()) admission = await PrepareAsync(seed, size: 10_485_761);
        var fail = new FailRepair(); await using var s = Open(interceptor: fail);
        var error = await Should.ThrowAsync<DbUpdateException>(() => Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct));
        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("synthetic repair rollback");
        await using (var verify = Open()) (await verify.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync();
        attempt.State.ShouldBe("failed"); attempt.FailureCategory.ShouldBe("encoded_image_too_large");
        (await s.Context.Set<VetPhotoSource>().CountAsync()).ShouldBe(1); (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_queue_fails_closed_when_family_context_is_absent_or_wrong(bool absent)
    {
        await SeedAsync(); await using var s = absent ? Unscoped() : Open(FamilyId + 1);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct));
        error.Message.ShouldBe("Vet scope is not active.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exact_current_or_historical_scheduled_queue_uses_decision_actor_and_never_restores_cancelled_batch(bool historical)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        var scheduled = await ScheduleAsync(s, admission, historical, actor: 222);
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"));
        await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "cancelled").SetProperty(c => c.RequiresExplicitRestoration, true));
        var before = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json);
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBe([new(Scope, admission.Source!.Id, admission.Input!.Id, 222, scheduled.Attempt.Id)]);
        (await Store(s).ReadInputAsync(Scope, admission.Source.Id, admission.Input.Id, 222, Ct))!.Caption.ShouldBe("synthetic caption");
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json).ShouldBe(before);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoBatch>().SingleAsync()).State.ShouldBe("cancelled");
    }

    [Theory]
    [InlineData("run_cancelled")] [InlineData("ordinal")] [InlineData("window_state")] [InlineData("actor")]
    [InlineData("run_model")] [InlineData("run_topic")] [InlineData("window_topic")] [InlineData("attempt_topic")]
    [InlineData("run_telegram")] [InlineData("window_telegram")] [InlineData("attempt_telegram")]
    [InlineData("selection")] [InlineData("undelivered")] [InlineData("profile")]
    [InlineData("ref_deleted")] [InlineData("ref_revision")] [InlineData("candidate_revision")]
    [InlineData("source_edit")] [InlineData("binding")] [InlineData("member")]
    public async Task Scheduled_queue_requires_exact_approved_manifest_grants_and_frozen_source(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        var scheduled = await ScheduleAsync(s, admission);
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled")); // No ordinary work masks scheduled fences.
        switch (invalid)
        {
            case "run_cancelled": await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.CancelledAt, Now)); break;
            case "ordinal": await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.NextWindowOrdinal, 1)); break;
            case "window_state": await s.Context.Set<VetPhotoRunWindow>().ExecuteUpdateAsync(u => u.SetProperty(w => w.State, "awaiting_review")); break;
            case "actor": await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == scheduled.Attempt.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ActorUserId, 222)); break;
            case "run_model": await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.ModelName, "other-model")); break;
            case "run_topic": await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.TopicId, 8)); break;
            case "window_topic": await s.Context.Set<VetPhotoRunWindow>().ExecuteUpdateAsync(u => u.SetProperty(w => w.TopicId, 8)); break;
            case "attempt_topic": await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == scheduled.Attempt.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.TopicId, 8)); break;
            case "run_telegram": await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.TelegramBotId, 2001)); break;
            case "window_telegram": await s.Context.Set<VetPhotoRunWindow>().ExecuteUpdateAsync(u => u.SetProperty(w => w.TelegramBotId, 2001)); break;
            case "attempt_telegram": await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == scheduled.Attempt.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramBotId, 2001)); break;
            case "selection": await s.Context.Set<VetPhotoRunWindow>().ExecuteUpdateAsync(u => u.SetProperty(w => w.SelectionJson, "[]")); break;
            case "undelivered": await s.Context.Set<VetPhotoReview>().ExecuteUpdateAsync(u => u.SetProperty(r => r.CompletePreviewDelivered, false)); break;
            case "profile": await s.Context.Set<VetPhotoReview>().ExecuteUpdateAsync(u => u.SetProperty(r => r.ProfileRevision, 999)); break;
            case "ref_deleted": await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "deleted")); break;
            case "ref_revision": await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(r => r.Revision, 2)); break;
            case "candidate_revision": await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, 2)); break;
            case "source_edit": await CaptionEditAsync(s, admission, archive: false); break;
            case "binding": await s.Context.Messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.TelegramMessageId, 900)); break;
            case "member": await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied)); break;
        }
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var persisted = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == scheduled.Attempt.Id);
        persisted.State.ShouldBe("queued"); persisted.ReservedResultSlot.ShouldBeFalse();
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Persisted_success_and_caption_reuse_are_not_due_but_direct_replay_retains_exact_evidence_and_version()
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        const string version = "v1-synthetic-prompt-version";
        var completed = await CompleteAsync(s, admission, version); completed.Status.ShouldBe(VetPhotoImageStatus.Installed);
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var replay = (await Store(s).ReadStoredImageAsync(Scope, admission.Source!.Id, admission.Input!.Id, null, 222, Ct)).ShouldNotBeNull();
        replay.Extraction!.Id.ShouldBe(completed.Extraction!.Id); replay.Extraction.PromptVersion.ShouldBe(version);
        var edited = await CaptionEditAsync(s, admission);
        var reused = await Store(s).ReuseDisplayAsync(Scope, admission.Source.Id, edited.Input!.Id, 222, "gpt-6.1-sol", Ct, version);
        reused.Status.ShouldBe(VetPhotoImageStatus.Reused); reused.Extraction!.ReusesExtractionId.ShouldBe(completed.Extraction.Id);
        reused.Extraction.PromptVersion.ShouldBe(version);
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(2); (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("different")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("surrogate")]
    public async Task Changed_or_invalid_prompt_version_cannot_reuse_or_add_an_unvalidated_result(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        await CompleteAsync(s, admission, "v1-synthetic-old-prompt"); var edited = await CaptionEditAsync(s, admission);
        var version = invalid switch { "different" => "v1-synthetic-new-prompt", "empty" => "", "oversized" => new string('x', 31), _ => "\ud800" };
        var reused = await Store(s).ReuseDisplayAsync(Scope, admission.Source!.Id, edited.Input!.Id, 111, "gpt-6.1-sol", Ct, version);
        reused.Status.ShouldBe(invalid == "different" ? VetPhotoImageStatus.NotFound : VetPhotoImageStatus.Refused);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "reuse")).ShouldBe(0);
    }

    [Theory]
    [InlineData(30, true)] [InlineData(31, false)] [InlineData(0, false)]
    public async Task Completion_prompt_version_has_exact_scalar_storage_bound(int length, bool valid)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        var result = await CompleteAsync(s, admission, new string('x', length));
        result.Status.ShouldBe(valid ? VetPhotoImageStatus.Installed : VetPhotoImageStatus.InvalidResult);
        result.Extraction!.PromptVersion.ShouldBe(valid ? new string('x', length) : "photo-v1");
        result.Extraction.FailureCategory.ShouldBe(valid ? null : "invalid_prompt_version");
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Queue_and_direct_input_are_private_to_exact_family_and_receiving_bot()
    {
        await SeedAsync(); VetPhotoAdmission first;
        await using (var seed = Open()) first = await PrepareAsync(seed);
        var family = new Family { Name = "synthetic second family", CreatedAt = Now }; Db.Add(family); await Db.SaveChangesAsync();
        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 2001, Username = "synthetic_second_vet_bot", Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(bot); Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 333, DisplayName = "synthetic owner", IsOwner = true,
            Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now }); await Db.SaveChangesAsync();
        Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 7, Title = "synthetic topic", Status = PlaceStatus.Approved, CreatedAt = Now }); await Db.SaveChangesAsync();
        var other = new VetDiaryScope(family.Id, bot.Id, bot.TelegramBotId, -200, 7); await using var foreign = Open(family.Id);
        await foreign.Profiles.GetOrCreateAsync(family.Id, bot.Id, Ct);
        var second = await PrepareAsync(foreign, 2, selectedScope: other, actor: 333);
        (await Store(foreign).GetDueAsync(family.Id, bot.Id, 5, Ct)).ShouldBe([new(other, second.Source!.Id, second.Input!.Id, 333)]);
        (await Store(foreign).ReadInputAsync(other, first.Source!.Id, first.Input!.Id, 333, Ct)).ShouldBeNull();
        await using var own = Open(); (await Store(own).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBe([new(Scope, first.Source.Id, first.Input.Id, 111)]);
        (await Store(own).ReadInputAsync(Scope, second.Source.Id, second.Input.Id, 111, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("manual", false)] [InlineData("linked", false)] [InlineData("excluded", true)]
    [InlineData("cancelled", true)] [InlineData("deleted", true)]
    public async Task Readonly_stored_replay_preserves_protected_candidate_and_returns_explicit_delta(string state, bool restoration)
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true); var result = await CompleteAsync(s, admission);
        await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.State, state == "manual" ? "awaiting_context" : state)
            .SetProperty(c => c.ManuallyCorrected, state == "manual").SetProperty(c => c.RequiresExplicitRestoration, restoration));
        var before = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json);
        var stored = (await Store(s).ReadStoredImageAsync(Scope, admission.Source!.Id, admission.Input!.Id, null, 222, Ct)).ShouldNotBeNull();
        stored.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta); stored.Extraction!.Id.ShouldBe(result.Extraction!.Id);
        stored.Delta!.RequiresExplicitRestoration.ShouldBe(restoration);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json).ShouldBe(before);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await Store(s).ReadStoredImageAsync(Scope with { TopicId = 8 }, admission.Source.Id, admission.Input.Id, null, 222, Ct)).ShouldBeNull();
        (await Store(s).ReadStoredImageAsync(Scope, admission.Source.Id, admission.Input.Id, null, 333, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Invalid_scalar_prompt_version_is_not_stored_as_provider_metadata()
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        var result = await CompleteAsync(s, admission, "\ud800"); result.Status.ShouldBe(VetPhotoImageStatus.InvalidResult);
        result.Extraction!.PromptVersion.ShouldBe("photo-v1"); result.Extraction.FailureCategory.ShouldBe("invalid_prompt_version");
        result.Extraction.StructuredJson.ShouldBe("{}"); (await s.Context.Set<VetPhotoCandidate>().SingleAsync()).ExtractionResultId.ShouldBeNull();
    }

    [Fact]
    public async Task Completed_batch_only_releases_an_existing_source_edit_as_protected_evidence_work()
    {
        await SeedAsync(); await using var s = Open(); var admission = await PrepareAsync(s, archive: true);
        await CompleteAsync(s, admission);
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "completed"));
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var edited = await CaptionEditAsync(s, admission);
        (await Store(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBe([new(Scope, admission.Source!.Id, edited.Input!.Id, 111)]);
        (await s.Context.Set<VetPhotoBatch>().SingleAsync()).State.ShouldBe("completed");
        (await s.Context.Set<VetPhotoSource>().SingleAsync()).CurrentOrdinal.ShouldBe(2);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1); (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    private async Task<VetPhotoAdmission> CaptionEditAsync(VetTestSession s, VetPhotoAdmission prior, bool archive = true)
    {
        var source = prior.Source!;
        var result = await Store(s).AdmitAsync(Scope, ImageMessage(source.TelegramMessageId, Scope, "synthetic changed caption")
            with { IsEdit = true, EditedAt = Now.AddSeconds(1) }, 100, Attachment(source.TelegramMessageId), null, Ct);
        result.Input!.ReusesImageInputId.ShouldBe(prior.Input!.Id);
        if (archive) (await Store(s).ReserveDownloadAsync(Scope, source.Id, result.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        return (await Store(s).GetSourceAsync(Scope, source.Id, Ct)).ShouldNotBeNull();
    }
    private T ScopedFixture<T>(VetTestSession s, T value) where T : class
    {
        var entry = s.Context.Entry(value); entry.Property("FamilyId").CurrentValue = Scope.FamilyId;
        entry.Property("BotDbId").CurrentValue = Scope.BotDbId; entry.Property("TelegramBotId").CurrentValue = Scope.TelegramBotId;
        entry.Property("ChatId").CurrentValue = Scope.ChatId; entry.Property("TopicId").CurrentValue = Scope.TopicId; return value;
    }
    private sealed record Schedule(VetPhotoAttempt Attempt, VetPhotoRun Run, VetPhotoRunWindow Window, VetPhotoReview Review);
    private async Task<Schedule> ScheduleAsync(VetTestSession s, VetPhotoAdmission selected, bool historical = false, long actor = 111)
    {
        if (historical) await CaptionEditAsync(s, selected);
        var source = (await Store(s).GetSourceAsync(Scope, selected.Source!.Id, Ct)).ShouldNotBeNull().Source!.ShouldNotBeNull();
        var reference = await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(r => r.InputRevisionId == selected.Input!.Id);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == source.Id);
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        var key = Guid.NewGuid();
        var selection = JsonSerializer.Serialize(new[] { new VetPhotoRunInputSnapshot(source.Id, selected.Input!.Id, reference.Id,
            reference.Revision, source.CurrentInputRevisionId, source.CurrentOrdinal, candidate.Revision, key) }, Json);
        const string page = "synthetic re-extraction selection preview";
        var review = ScopedFixture(s, new VetPhotoReview
        {
            Id = Guid.NewGuid(), BatchId = source.BatchId, OperationKey = Guid.NewGuid(), Kind = "reextract_selection",
            State = "accepted", RequesterUserId = 111, DecisionActorUserId = actor, SelectionJson = selection,
            ProfileId = profile.Id, ProfileRevision = profile.Revision,
            Fingerprint = Hash(selection), PreviewPagesJson = JsonSerializer.Serialize(new[] { page }, Json),
            DeliveredPagesJson = JsonSerializer.Serialize(new[] { new VetPhotoPageDelivery(0, 700, Hash(page)) }, Json),
            PageCount = 1, CompletePreviewDelivered = true, AcceptancePromptMessageId = 700, CreatedAt = Now, DecidedAt = Now
        });
        s.Context.Add(review); await s.Context.SaveChangesAsync();
        var run = ScopedFixture(s, new VetPhotoRun
        {
            Id = Guid.NewGuid(), ActorUserId = actor, OperationKey = Guid.NewGuid(), SelectionReviewId = review.Id,
            SelectionMode = historical ? "all_originals" : "current", SelectionJson = selection, SelectedCount = 1,
            ModelName = "gpt-6.1-sol", State = "approved", CreatedAt = Now
        });
        s.Context.Add(run); await s.Context.SaveChangesAsync();
        var window = ScopedFixture(s, new VetPhotoRunWindow
        { Id = Guid.NewGuid(), RunId = run.Id, State = "queued", SelectionJson = selection, CreatedAt = Now });
        s.Context.Add(window); await s.Context.SaveChangesAsync();
        var attempt = ScopedFixture(s, new VetPhotoAttempt
        {
            Id = key, SourceId = source.Id, InputRevisionId = selected.Input!.Id, RunWindowId = window.Id, ActorUserId = actor,
            Kind = "image", State = "queued", ExpectedCurrentInputId = source.CurrentInputRevisionId,
            ExpectedSourceOrdinal = source.CurrentOrdinal, HistoricalSelection = historical, CreatedAt = Now, UpdatedAt = Now
        });
        s.Context.Add(attempt); await s.Context.SaveChangesAsync(); return new(attempt, run, window, review);
    }
    private sealed class ObserveSql : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
    private sealed class FailRepair : DbCommandInterceptor
    {
        private bool failed;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!failed && command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("UPDATE vet_photo_attempts", StringComparison.OrdinalIgnoreCase))
            { failed = true; throw new InvalidOperationException("synthetic repair rollback"); }
            return ValueTask.FromResult(result);
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static byte[] ImageBytes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }

}
