using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Application.Messages;
using Assistant.Domain.Llm;
using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Vet;

public sealed class VetWorkflowTests : VetTestBase
{
    [Theory]
    [InlineData(null)]
    [InlineData("undo")]
    [InlineData("correct")]
    [InlineData("accept")]
    public async Task Stale_photo_operation_cannot_suppress_current_reading_or_dispatch_history_action(string? alternateOperation)
    {
        await SeedAsync(); var photos = new ObservedPhotos(); await using var s = Open(photos: photos);
        var root = JsonNode.Parse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"}],"photo_operation":{"kind":"correct","corrections":[{"value":"8.2"}]}}""")!;
        if (alternateOperation is not null) root["operation"] = new JsonObject { ["kind"] = alternateOperation };
        var json = root.ToJsonString();
        s.Chat.EnqueueResponse(json);
        var update = new IncomingUpdate(1, Text("Current glucose 6.4 mmol/L"));
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        photos.Dispatched.ShouldBeEmpty();
        var saved = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        saved.Value.ShouldBe(6.4m); saved.OccurredAt.ShouldBe(Now);
        saved.TelegramMessageId.ShouldBe(update.Message!.MessageId);
        JsonNode.DeepEquals(JsonNode.Parse((await s.Context.VetExtractionResults.SingleAsync()).Json), JsonNode.Parse(json)).ShouldBeTrue();
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        photos.Dispatched.ShouldBeEmpty(); (await s.Context.VetEvents.CountAsync()).ShouldBe(1);
        (await s.Context.LlmCalls.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Grounded_contextual_photo_correction_preserves_independent_administered_insulin()
    {
        await SeedAsync(); var photos = new ObservedPhotos(); await using var s = Open(photos: photos);
        var target = Guid.Parse("44444444-4444-4444-8444-444444444444");
        s.Chat.EnqueueResponse(JsonSerializer.Serialize(new { needs_reply = false, events = new[]
        { new { type = "insulin", intent = "record", dose = "0.125", time_evidence = "current" } },
            photo_operation = new { kind = "correct", action_evidence = "correct this photo to 6.4", source_ids = new[] { target } } }));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("correct this photo to 6.4; administered insulin 0.125 U")), CancellationToken.None);
        photos.Dispatched.Single().SourceIds.Single().ShouldBe(target);
        var insulin = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        insulin.EventType.ShouldBe("insulin"); insulin.Value.ShouldBe(0.125m); insulin.OccurredAt.ShouldBe(Now);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        (await s.Context.LlmCalls.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ungrounded_photo_action_without_current_records_clarifies_without_mutation(bool questionCandidate)
    {
        await SeedAsync(); var photos = new ObservedPhotos(); await using var s = Open(photos: photos);
        s.Chat.EnqueueResponse(JsonSerializer.Serialize(new { needs_reply = false,
            events = questionCandidate ? new[] { new { type = "glucose", intent = "question_only", value = "6.4" } } : [],
            photo_operation = new { kind = "correct", action_evidence = "correct the historical photo" } }));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic neutral current message")), CancellationToken.None);
        photos.Dispatched.ShouldBeEmpty(); (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        s.Telegram.SentMessages.Single().Text.ShouldContain("прежний запрос не повторён");
        (await s.Context.VetTextSourceRevisions.SingleAsync()).State.ShouldBe("completed");
    }

    private sealed class ObservedPhotos : IVetPhotoAssistant
    {
        public List<VetPhotoOperation> Dispatched { get; } = [];
        public Task<bool> OperationAsync(ReceivingBot b, ITelegramClient c, IncomingMessage m, VetPhotoOperation o, Guid key, CancellationToken ct)
        { Dispatched.Add(o); return Task.FromResult(true); }
        public Task<string> DescribeAsync(VetDiaryScope s, long actor, CancellationToken ct) => Task.FromResult("Historical source 44444444-4444-4444-8444-444444444444, correction: 8.2; reference only.");
        public Task<VetInterpretation> FilterCaptionAsync(VetDiaryScope s, int id, VetInterpretation i, CancellationToken ct) => Task.FromResult(i);
        public Task<VetPhotoAdmission?> AdmitAsync(ReceivingBot b, IncomingMessage m, long id, VetAdmittedSource? a, CancellationToken ct) => Task.FromResult<VetPhotoAdmission?>(null);
        public Task BindAsync(ReceivingBot b, IncomingMessage m, StoreResult s, VetPhotoAdmission? a, CancellationToken ct) => Task.CompletedTask;
        public Task HandleAdmissionAsync(ReceivingBot b, ITelegramClient c, IncomingMessage m, StoreResult s, VetPhotoAdmission? a, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CommandAsync(ReceivingBot b, ITelegramClient c, IncomingMessage m, string command, string? args, Guid key, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> HandleCallbackAsync(ReceivingBot b, ITelegramClient c, CallbackQueryInfo q, CancellationToken ct) => Task.FromResult(false);
        public Task ResumeAsync(ReceivingBot b, ITelegramClient c, CancellationToken ct) => Task.CompletedTask;
        public Task WorkFinishedAsync(ReceivingBot b, ITelegramClient c, VetPhotoWork w, VetPhotoProcessResult r, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_duplicate_model_candidates_save_once_but_distinct_times_and_sources_remain(bool distinctTimes)
    {
        await SeedAsync(); await using var s = Open();
        var json = JsonSerializer.Serialize(new { needs_reply = false, events = new[]
        {
            new { type = "glucose", intent = "record", value = "6.4", unit = "mmol/L",
                date = "2031-05-12", time = "09:00", offset = "+00:00", time_evidence = "stated" },
            new { type = "glucose", intent = "record", value = "6.4", unit = "mmol/L",
                date = "2031-05-12", time = distinctTimes ? "09:01" : "09:00", offset = "+00:00", time_evidence = "stated" }
        } });
        s.Chat.EnqueueResponse(json);
        var update = new IncomingUpdate(1, Text("synthetic explicit measurement"));
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        var saved = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.OccurredAt).ToArrayAsync();
        saved.Length.ShouldBe(distinctTimes ? 2 : 1);
        saved.Select(e => e.Value).ShouldBe(distinctTimes ? new[] { 6.4m, 6.4m } : new[] { 6.4m });
        saved.Select(e => e.OccurredAt).ShouldBe(distinctTimes
            ? new[] { DateTimeOffset.Parse("2031-05-12T09:00:00Z"), DateTimeOffset.Parse("2031-05-12T09:01:00Z") }
            : new[] { DateTimeOffset.Parse("2031-05-12T09:00:00Z") });
        saved.Select(e => e.CandidateOrdinal).ShouldBe(distinctTimes ? new[] { 0, 1 } : new[] { 0 });
        JsonNode.DeepEquals(JsonNode.Parse((await s.Context.VetExtractionResults.AsNoTracking().SingleAsync()).Json), JsonNode.Parse(json)).ShouldBeTrue();
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(saved.Length);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        (await s.Context.LlmCalls.CountAsync()).ShouldBe(1);
        s.Chat.EnqueueResponse(json);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic independent measurement", 1001)), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(saved.Length * 2);
        (await s.Context.VetEvents.Select(e => e.SourceId).Distinct().CountAsync()).ShouldBe(2);
        var retained = await s.Context.VetExtractionResults.Select(e => e.Json).ToArrayAsync();
        retained.Length.ShouldBe(2);
        foreach (var result in retained)
            JsonNode.DeepEquals(JsonNode.Parse(result), JsonNode.Parse(json)).ShouldBeTrue();
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(2);
        (await s.Context.LlmCalls.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Targetless_clarification_cannot_replace_both_same_type_pending_candidates()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","date":"2001-04-03"},{"type":"glucose","intent":"record","value":"6.8","date":"2001-04-03"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic two incomplete readings")), CancellationToken.None);
        var before = await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync();
        var prompt = s.Telegram.ButtonMessages.Single();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","time":"09:30","time_evidence":"stated"}],"operation":{"kind":"accept"}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic targetless clock clarification", 1001)
            with { ReplyToMessageId = prompt.MessageId, ReplyToUserId = Bot.TelegramBotId }), CancellationToken.None);
        var after = await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync();
        after.ReviewRevision.ShouldBe(before.ReviewRevision); after.ProposalJson.ShouldBe(before.ProposalJson);
        after.PromptMessageId.ShouldBe(before.PromptMessageId); after.State.ShouldBe("pending");
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        s.Telegram.SentMessages.Last().Text.ShouldContain("Неоднозначное уточнение не меняет просмотр");
    }

    [Fact]
    public async Task Oversized_partial_preview_cannot_save_full_subset_by_natural_acceptance()
    {
        await SeedAsync(); await using var s = Open();
        var json = JsonSerializer.Serialize(new { needs_reply = false, events = Enumerable.Range(1, 20).Select(index =>
            new { type = "insulin", intent = "unsure", dose = index.ToString(), product = new string('x', 100), time_evidence = "current" }).ToArray() });
        s.Chat.EnqueueResponse(json);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic oversized proposed subset")), CancellationToken.None);
        var pending = await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync();
        s.Telegram.ButtonMessages.Single().Buttons.Select(b => b.Label).ShouldBe(new[] { "Отменить" });
        s.Chat.EnqueueResponse(JsonSerializer.Serialize(new { needs_reply = false, events = Array.Empty<object>(),
            operation = new { kind = "accept", pending_id = pending.Id, review_revision = pending.ReviewRevision } }));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic yes to partial preview", 1001)), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        (await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync()).State.ShouldBe("pending");
        JsonSerializer.Deserialize<VetProposal>(pending.ProposalJson)!.RequiresClarification.ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_preview_delivery_cannot_be_accepted_by_explicit_model_reference_without_a_new_review()
    {
        await SeedAsync(); await using var s = Open();
        s.Telegram.ThrowOnButtonsToChatId = -100;
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"unsure","value":"6.4","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic unseen pending")), CancellationToken.None);
        var pending = await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync();
        pending.PromptMessageId.ShouldBeNull();
        s.Telegram.ThrowOnButtonsToChatId = null;
        s.Chat.EnqueueResponse(JsonSerializer.Serialize(new { needs_reply = false, events = Array.Empty<object>(),
            operation = new { kind = "accept", pending_id = pending.Id, review_revision = pending.ReviewRevision } }));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic model-selected unseen review", 1001)), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        s.Telegram.ButtonMessages.Single().Text.ShouldContain("глюкоза 6.4 mmol/L");
        s.Telegram.SentMessages.Last().Text.ShouldContain("Сначала проверьте полный отправленный просмотр");
        var shown = await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync();
        shown.PromptMessageId.ShouldBe(s.Telegram.ButtonMessages.Single().MessageId);
        s.Chat.EnqueueResponse(JsonSerializer.Serialize(new { needs_reply = false, events = Array.Empty<object>(),
            operation = new { kind = "accept", pending_id = shown.Id, review_revision = shown.ReviewRevision } }));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("synthetic separate reviewed confirmation", 1002, 222)), CancellationToken.None);
        var saved = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        saved.Value.ShouldBe(6.4m); saved.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync()).ActorUserId.ShouldBe(222);
        (await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync()).State.ShouldBe("accepted");
    }

    [Fact]
    public async Task Value_only_correction_preserves_old_time_even_when_timezone_default_is_cleared()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Historical);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic old measurement")), CancellationToken.None);
        var original = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("/settz -", 1001)), CancellationToken.None);
        s.Chat.EnqueueResponse($$$"""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.9","event_id":{{{original.Id}}}}],"operation":{"kind":"correct"}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("synthetic value-only correction", 1002, 222)), CancellationToken.None);
        var current = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        current.Id.ShouldBe(original.Id); current.Value.ShouldBe(6.9m); current.Revision.ShouldBe(2);
        current.OccurredAt.ShouldBe(original.OccurredAt); current.LocalTime.ShouldBe(original.LocalTime);
        current.TimeZoneSnapshot.ShouldBe(original.TimeZoneSnapshot); current.OccurredAtSource.ShouldBe(original.OccurredAtSource);
    }

    [Fact]
    public async Task Incomplete_manual_correction_keeps_exact_target_until_clarification_review_is_confirmed()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Historical);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic old measurement")), CancellationToken.None);
        var original = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        s.Chat.EnqueueResponse($$$"""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.9","date":"2001-04-04","event_id":{{{original.Id}}}}],"operation":{"kind":"correct"}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic incomplete correction", 1001)), CancellationToken.None);
        var incomplete = s.Telegram.ButtonMessages.Single();
        incomplete.Buttons.Select(b => b.Label).ShouldBe(new[] { "Отменить" });
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","time":"10:15","time_evidence":"stated"}],"operation":{"kind":"accept"}}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("synthetic clarification", 1002, 222)
            with { ReplyToMessageId = incomplete.MessageId, ReplyToUserId = Bot.TelegramBotId }), CancellationToken.None);
        var complete = s.Telegram.ButtonMessages.Last();
        complete.Text.ShouldContain("#" + original.Id);
        complete.Text.ShouldContain("2001-04-04 10:15:00");
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram, new("confirm", 222, complete.Buttons[0].CallbackData,
            -100, complete.MessageId, 7, "supergroup"), CancellationToken.None);
        var current = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        current.Id.ShouldBe(original.Id); current.Value.ShouldBe(6.9m); current.Revision.ShouldBe(2);
        current.OccurredAt.ShouldBe(DateTimeOffset.Parse("2001-04-04T10:15:00Z"));
        current.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.VetDiaryActions.OrderByDescending(a => a.Id).FirstAsync()).ActorUserId.ShouldBe(222);
    }

    private const string Mixed = """{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"},{"type":"insulin","intent":"record","dose":"0.125","time_evidence":"current"}],"unclear":[]}""";
    private const string Question = """{"needs_reply":true,"events":[],"unclear":[]}""";
    private const string Historical = """{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","date":"2001-04-03","time":"09:30","unit":"mmol/L","time_evidence":"stated"}],"unclear":[]}""";

    [Fact]
    public async Task Source_edit_removal_and_undo_preserve_ids_and_higher_revisions_without_deleting_source()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Mixed);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic mixed source")), CancellationToken.None);
        var original = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic source with insulin removed")
            with { IsEdit = true, EditedAt = Now.AddMinutes(1) }), CancellationToken.None);
        var removed = (await s.Diary.GetEventAsync(Scope, original[1].Id, CancellationToken.None))!;
        removed.DeletedAt.ShouldNotBeNull(); removed.DeleteReason.ShouldBe("source_edit"); removed.Revision.ShouldBe(2);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("/undo", 1001)), CancellationToken.None);
        var restored = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        restored.Select(e => e.Id).ShouldBe(original.Select(e => e.Id));
        restored.Select(e => e.Revision).ShouldBe(new[] { 1, 3 }); restored.ShouldAllBe(e => e.DeletedAt == null);
        (await s.Context.VetTextSources.CountAsync()).ShouldBe(2);
        (await s.Context.VetTextSourceRevisions.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Older_source_edit_and_expired_pending_do_not_mutate_confirmed_diary()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Mixed);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic mixed source")), CancellationToken.None);
        s.Chat.EnqueueResponse(Mixed.Replace("6.4", "6.8"));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic too-old edit")
            with { IsEdit = true, EditedAt = Now.AddHours(25) }), CancellationToken.None);
        (await s.Context.VetEvents.Where(e => e.EventType == "glucose").SingleAsync()).Value.ShouldBe(6.4m);
        s.Telegram.SentMessages.Last().Text.ShouldContain("Исправьте запись явно по ID");
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"unsure","dose":"0.3","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("synthetic pending", 1001)), CancellationToken.None);
        var prompt = s.Telegram.ButtonMessages.Single(); Clock.UtcNow = Now.AddHours(25);
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram, new("expired", 222, prompt.Buttons[0].CallbackData,
            -100, prompt.MessageId, 7, "supergroup"), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(2);
        s.Telegram.AnsweredCallbacks.Single().Text.ShouldBe("Этот просмотр уже недействителен.");
        (await s.Diary.GetPendingAsync(Scope, CancellationToken.None)).ShouldBeEmpty();
        (await s.Context.VetPendingDecisions.AsNoTracking().SingleAsync()).State.ShouldBe("expired");
    }

    [Fact]
    public async Task Natural_profile_change_is_owner_only_and_commands_do_not_call_model_or_create_diary_actions()
    {
        await SeedAsync(); await using var s = Open();
        var profileChange = """{"needs_reply":false,"events":[],"operation":{"kind":"profile","profile_changes":[{"field":"ReportedVetGuidance","value":"synthetic owner-reported guidance"}]}}""";
        s.Chat.EnqueueResponse(profileChange);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic member profile change", actor: 222)), CancellationToken.None);
        (await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, CancellationToken.None)).ReportedVetGuidance.ShouldBeNull();
        s.Telegram.SentMessages.Last().Text.ShouldBe(VetAssistant.OwnerOnlyText);
        s.Chat.EnqueueResponse(profileChange);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic owner profile change", 1001)), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("/setnote owner synthetic owner context", 1002)), CancellationToken.None);
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, CancellationToken.None);
        profile.ReportedVetGuidance.ShouldBe("synthetic owner-reported guidance");
        profile.OwnerContextNote.ShouldBe("synthetic owner context");
        var provenance = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(profile.FieldProvenanceJson)!;
        provenance["ReportedVetGuidance"].GetProperty("ActorUserId").GetInt64().ShouldBe(111);
        provenance["ReportedVetGuidance"].GetProperty("Source").GetString().ShouldBe("owner-reported veterinarian");
        s.Chat.RequestedMessages.Count.ShouldBe(2);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0); (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Mixed_actual_report_saves_one_action_once_under_transport_redelivery_with_actual_attempt_accounting()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Mixed, 120, 40);
        var message = Text("synthetic measured glucose and administered fractional insulin");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, message), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, message), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, message), CancellationToken.None);
        var events = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        events.Select(e => e.Value).ShouldBe(new[] { 6.4m, 0.125m });
        events.ShouldAllBe(e => e.OccurredAt == Now && e.Revision == 1);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        var reply = s.Telegram.SentMessages.ShouldHaveSingleItem().Text;
        reply.ShouldContain("6.4 mmol/L"); reply.ShouldContain("0.125 U"); reply.ShouldContain("время исходного сообщения");
        var call = await s.Context.LlmCalls.AsNoTracking().SingleAsync();
        call.Tier.ShouldBe("fast"); call.InputTokens.ShouldBe(120); call.OutputTokens.ShouldBe(40); call.Cost.ShouldBe(0);
        call.TriggerMessageId.ShouldBe(events[0].SourceMessageDbId); call.BotId.ShouldBe(Bot.TelegramBotId);
        call.ChatId.ShouldBe(-100); call.TopicId.ShouldBe(7); call.Outcome.ShouldBe(LlmCallOutcome.Ok);
    }

    [Theory]
    [InlineData(7, false, true)]
    [InlineData(8, false, false)]
    [InlineData(8, true, true)]
    public async Task Reply_eligibility_uses_exact_topic_setting_or_explicit_addressing(int topic, bool addressed, bool answered)
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Question); if (answered) s.Chat.EnqueueResponse("synthetic consultation");
        var message = Text((addressed ? "@synthetic_vet_bot " : "") + "synthetic indirect request", topic: topic);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, message), CancellationToken.None);
        s.Chat.RequestedMessages.Count.ShouldBe(answered ? 2 : 1);
        var calls = await s.Context.LlmCalls.OrderBy(c => c.Id).ToListAsync();
        calls.Select(c => c.Tier).ShouldBe(answered ? new[] { "fast", "smart" } : new[] { "fast" });
        s.Telegram.SentMessages.Count.ShouldBe(answered ? 1 : 0);
        (await s.Context.Messages.CountAsync(m => m.Direction == Assistant.Domain.Messages.MessageDirection.Out)).ShouldBe(answered ? 1 : 0);
    }

    [Fact]
    public async Task Chatter_with_false_reply_intent_and_other_bot_addressing_remain_quiet()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[],"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("@synthetic_vet_bot synthetic chatter")), CancellationToken.None);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("@another_bot synthetic question", 1001)), CancellationToken.None);
        s.Telegram.SentMessages.ShouldBeEmpty(); s.Chat.RequestedMessages.Count.ShouldBe(1);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Clear_fact_pending_sibling_and_requested_answer_are_independent()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"},{"type":"insulin","intent":"record","dose":"0.3","date":"2001-04-03","time_evidence":"historical"}],"unclear":[]}""");
        s.Chat.EnqueueResponse("synthetic answer acknowledging incomplete history");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic report with old administration and question")), CancellationToken.None);
        var row = await s.Context.VetEvents.SingleAsync(); row.EventType.ShouldBe("glucose");
        var pending = await s.Context.VetPendingDecisions.SingleAsync(); pending.State.ShouldBe("pending");
        s.Chat.RequestedMessages.Count.ShouldBe(2);
        s.Telegram.SentMessages.Select(m => m.Text).ShouldContain("synthetic answer acknowledging incomplete history");
        s.Telegram.ButtonMessages.Single().Buttons.Select(b => b.Label).ShouldBe(new[] { "Отменить" });
        s.Chat.RequestedMessages[1][0].Text!.ShouldNotContain("инсулин 0.3");
    }

    [Fact]
    public async Task Hypothetical_insulin_is_answered_without_diary_event_or_pending_decision()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[{"type":"insulin","intent":"question_only","dose":"0.125","unit":"U"}],"unclear":[]}""");
        s.Chat.EnqueueResponse("synthetic discussion");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic conditional administration question")), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Context.VetPendingDecisions.CountAsync()).ShouldBe(0);
        s.Telegram.SentMessages.Single().Text.ShouldBe("synthetic discussion");
    }

    [Fact]
    public async Task Missing_history_time_can_be_clarified_then_confirmed_by_another_member_with_new_evidence()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"record","dose":"0.3","date":"2001-04-03","time_evidence":"historical"}],"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic historical administered dose")), CancellationToken.None);
        var firstPrompt = s.Telegram.ButtonMessages.Single();
        var original = await s.Context.VetTextSources.AsNoTracking().SingleAsync();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"record","dose":"0.3","date":"2001-04-03","time":"09:15","unit":"U","time_evidence":"stated"}],"operation":{"kind":"accept"},"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic clarification of time", 1001, 222) with { ReplyToMessageId = firstPrompt.MessageId, ReplyToUserId = Bot.TelegramBotId }), CancellationToken.None);
        var secondPrompt = s.Telegram.ButtonMessages.Last();
        secondPrompt.Buttons.Select(b => b.Label).ShouldBe(new[] { "Сохранить", "Отменить" });
        var clarification = await s.Context.VetTextSources.AsNoTracking().SingleAsync(x => x.TelegramMessageId == 1001);
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram,
            new("synthetic-callback", 222, secondPrompt.Buttons[0].CallbackData, -100, secondPrompt.MessageId, 7, "supergroup"), CancellationToken.None);
        var row = await s.Context.VetEvents.SingleAsync();
        row.EventType.ShouldBe("insulin"); row.Value.ShouldBe(0.3m); row.SourceAuthorUserId.ShouldBe(111);
        row.SourceId.ShouldBe(original.Id); row.InputRevisionId.ShouldBe(clarification.CurrentInputRevisionId);
        row.OccurredAt.ShouldBe(DateTimeOffset.Parse("2001-04-03T09:15:00Z"));
        (await s.Context.VetDiaryActions.SingleAsync()).ActorUserId.ShouldBe(222);
        (await s.Context.VetPendingDecisions.SingleAsync()).State.ShouldBe("accepted");
    }

    [Fact]
    public async Task Frozen_pending_defaults_do_not_change_after_profile_update_or_stale_button()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"unsure","dose":"0.125","time_evidence":"current"}],"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic ambiguous administered report")), CancellationToken.None);
        var prompt = s.Telegram.ButtonMessages.Single();
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("/setinsulin synthetic-new-product U", 1001)), CancellationToken.None);
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram,
            new("stale", 111, "v:a:1:1", -100, prompt.MessageId, 7, "supergroup"), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram,
            new("current", 222, prompt.Buttons[0].CallbackData, -100, prompt.MessageId, 7, "supergroup"), CancellationToken.None);
        var row = await s.Context.VetEvents.SingleAsync();
        row.Value.ShouldBe(0.125m); row.Product.ShouldBeNull();
        s.Chat.RequestedMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Edits_preserve_unchanged_ids_and_update_changed_fact_in_place_then_protect_manual_correction()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Mixed);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic original mixed report")), CancellationToken.None);
        var old = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        s.Chat.EnqueueResponse(Mixed.Replace("6.4", "6.8"));
        var edit = Text("synthetic changed mixed report") with { IsEdit = true, EditedAt = Now.AddMinutes(1) };
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, edit), CancellationToken.None);
        var current = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        current.Select(e => e.Id).ShouldBe(old.Select(e => e.Id));
        current.Select(e => e.Revision).ShouldBe(new[] { 2, 1 });
        current.Select(e => e.Value).ShouldBe(new[] { 6.8m, 0.125m });
        await s.Diary.ApplyAsync(new(Scope, Guid.NewGuid(), 222, "manual", current[0].ProfileId,
            [new(current[0].Id, 2, VetEventText.State(current[0]) with { Value = 7.1m })]), CancellationToken.None);
        s.Chat.EnqueueResponse(Mixed.Replace("6.4", "7.4"));
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, edit with { Text = "synthetic second source edit", EditedAt = Now.AddMinutes(2) }), CancellationToken.None);
        (await s.Diary.GetEventAsync(Scope, old[0].Id, CancellationToken.None))!.Value.ShouldBe(7.1m);
        (await s.Context.VetPendingDecisions.SingleAsync()).State.ShouldBe("pending");
    }

    [Fact]
    public async Task Older_history_list_and_natural_correction_use_persisted_records_and_no_extra_smart_call()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse(Historical);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic older glucose")), CancellationToken.None);
        var row = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        s.Chat.EnqueueResponse("""{"needs_reply":true,"events":[],"history_query":{"from_date":"2001-04-01","until_date":"2001-04-30","offset":0,"analysis":false},"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(2, Text("synthetic request for older month", 1001)), CancellationToken.None);
        s.Chat.RequestedMessages.Count.ShouldBe(2);
        s.Telegram.SentMessages.Last().Text.ShouldContain("#" + row.Id + " глюкоза 6.4 mmol/L 2001-04-03 09:30:00");
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.8","date":"2001-04-03","time":"09:30","unit":"mmol/L","time_evidence":"stated"}],"operation":{"kind":"correct"},"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(3, Text("synthetic correction by date and type", 1002, 222)), CancellationToken.None);
        var corrected = (await s.Diary.GetEventAsync(Scope, row.Id, CancellationToken.None))!;
        corrected.Value.ShouldBe(6.8m); corrected.Revision.ShouldBe(2); corrected.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.VetDiaryActions.OrderByDescending(a => a.Id).FirstAsync()).ActorUserId.ShouldBe(222);
    }

    [Fact]
    public async Task Persisted_result_and_exact_work_resume_after_commit_without_another_provider_call()
    {
        await SeedAsync(); Guid sourceId;
        await using (var interrupted = Open())
        {
            var e = await EvidenceAsync(interrupted);
            sourceId = e.Source.Source.Id;
            var plan = new VetTextPlan([new(null, null, e.State)], null);
            await interrupted.Diary.SaveWorkAsync(Scope, e.Source.Revision.Id, JsonSerializer.Serialize(plan), CancellationToken.None);
            await interrupted.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State), CancellationToken.None);
        }
        await using var recovered = Open();
        await recovered.Handler.ResumeAsync(Bot, recovered.Telegram, CancellationToken.None);
        recovered.Chat.RequestedMessages.ShouldBeEmpty();
        var source = (await recovered.Diary.GetSourceAsync(Scope, sourceId, CancellationToken.None))!;
        source.Revision.State.ShouldBe("completed");
        (await recovered.Context.VetEvents.SingleAsync()).Value.ShouldBe(6.4m);
        (await recovered.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Dispatching_source_pauses_on_restart_and_never_blindly_calls_provider()
    {
        await SeedAsync(); Guid sourceId;
        await using (var interrupted = Open())
        {
            var admitted = await interrupted.Diary.AdmitAsync(Scope, Text("synthetic pending dispatch"), 1, CancellationToken.None);
            var stored = await interrupted.Messages.StoreAsync(Bot.TelegramBotId, 1, Text("synthetic pending dispatch"), CancellationToken.None);
            sourceId = admitted.Source.Id;
            await interrupted.Diary.LinkMessageAsync(Scope, sourceId, stored.MessageDbId!.Value, CancellationToken.None);
            await interrupted.Diary.SetProcessingAsync(Scope, admitted.Revision.Id, "admitted", "dispatching", null, CancellationToken.None);
        }
        await using var recovered = Open();
        await recovered.Handler.ResumeAsync(Bot, recovered.Telegram, CancellationToken.None);
        await recovered.Handler.ResumeAsync(Bot, recovered.Telegram, CancellationToken.None);
        recovered.Chat.RequestedMessages.ShouldBeEmpty();
        (await recovered.Diary.GetSourceAsync(Scope, sourceId, CancellationToken.None))!.Revision.State.ShouldBe("paused");
        recovered.Telegram.SentMessages.Single().Text.ShouldBe(VetAssistant.PausedText);
        (await recovered.Context.VetEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Admitted_but_unstored_source_waits_for_transport_storage_and_links_before_provider_call()
    {
        await SeedAsync(); await using var s = Open();
        var message = Text("synthetic pre-offset admission");
        var admitted = await s.Diary.AdmitAsync(Scope, message, 1, CancellationToken.None);
        await s.Handler.ResumeAsync(Bot, s.Telegram, CancellationToken.None);
        s.Chat.RequestedMessages.ShouldBeEmpty();
        (await s.Messages.GetLastUpdateIdAsync(Bot.TelegramBotId, CancellationToken.None)).ShouldBe(0);
        s.Chat.EnqueueResponse(Mixed);
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, message), CancellationToken.None);
        (await s.Diary.GetSourceAsync(Scope, admitted.Source.Id, CancellationToken.None))!.Source.SourceMessageDbId.ShouldNotBeNull();
        (await s.Context.VetEvents.CountAsync()).ShouldBe(2);
        (await s.Context.LlmCalls.SingleAsync()).TriggerMessageId.ShouldBe(await s.Context.Messages.Where(m => m.TelegramMessageId == 1000).Select(m => m.Id).SingleAsync());
    }

    [Fact]
    public async Task Send_failure_keeps_committed_diary_action_and_redelivery_cannot_duplicate_it()
    {
        await SeedAsync(); await using var s = Open();
        s.Telegram.ThrowOnSendToChatId = -100; s.Chat.EnqueueResponse(Mixed);
        var update = new IncomingUpdate(1, Text("synthetic report with failed acknowledgement"));
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        s.Telegram.ThrowOnSendToChatId = null;
        await s.Handler.HandleAsync(Bot, s.Telegram, update, CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(2);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        s.Chat.RequestedMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Revoked_member_and_forged_place_callback_cannot_disclose_or_save_pending()
    {
        await SeedAsync(); await using var s = Open();
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"unsure","dose":"0.3","time_evidence":"current"}],"unclear":[]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic pending fact")), CancellationToken.None);
        var prompt = s.Telegram.ButtonMessages.Single();
        await Db.FamilyMembers.Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, Assistant.Domain.Families.FamilyMemberStatus.Denied));
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram, new("revoked", 222, prompt.Buttons[0].CallbackData, -100, prompt.MessageId, 7, "supergroup"), CancellationToken.None);
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram, new("forged", 111, prompt.Buttons[0].CallbackData, -100, prompt.MessageId, 8, "supergroup"), CancellationToken.None);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        s.Telegram.AnsweredCallbacks.Select(c => c.Text).ShouldBe(new[] { "У вас нет прав.", "Этот просмотр уже недействителен." });
        (await s.Context.VetPendingDecisions.SingleAsync()).State.ShouldBe("pending");
    }
}
