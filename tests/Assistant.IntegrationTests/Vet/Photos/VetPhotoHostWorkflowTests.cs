using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Llm;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Roles;
using Assistant.Infrastructure.Telegram;
using Assistant.Infrastructure.Vet;
using Assistant.Infrastructure.Vet.Photos;
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoHostWorkflowTests : VetTestBase
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private long update;
    private int message = 100;
    private int logical;
    private sealed record Item(Guid Source, Guid Input, Guid Batch, int Number, int Reading, int Message);
    private static decimal Value(int reading) => 5m + reading / 100m;
    private static DateTimeOffset At(int reading) => new(2031, 5, 12, 10 + reading / 60, reading % 60, 5, TimeSpan.Zero);
    private static byte[] Png(int id)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor((byte)id, (byte)(id / 256), 180));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    private Runtime Build(ImageProvider? provider = null, ReviewTelegram? telegram = null, IInterceptor? interceptor = null, VetPhotoCapacity? capacity = null)
    {
        provider ??= new(); telegram ??= new();
        var services = new ServiceCollection(); services.AddLogging(); services.AddApplication(); services.AddVetPersistence();
        services.RemoveAll<VetPhotoCapacity>(); services.AddSingleton(capacity ?? new VetPhotoCapacity());
        services.RemoveAll<IClock>(); services.AddSingleton<IClock>(Clock); services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentFamily, CurrentFamily>();
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<AssistantDbContext>(); AssistantDbContext.Configure(options, ConnectionString);
            if (interceptor != null) options.AddInterceptors(interceptor);
            return new AssistantDbContext(options.Options, sp.GetRequiredService<ICurrentFamily>());
        });
        services.AddSingleton(Options.Create(new BotOptions { ManagerToken = "synthetic-manager-token",
            TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" }));
        services.AddSingleton(new BuildInfo("abcdef1", null, Now));
        services.AddScoped<IMessageStore>(sp => new MessageStore(sp.GetRequiredService<AssistantDbContext>(), Clock, NullLogger<MessageStore>.Instance));
        services.AddScoped<Assistant.Application.Reminders.IReminderStore, Assistant.Infrastructure.Reminders.ReminderStore>();
            services.AddScoped<Assistant.Application.Expectations.IExpectationStore, Assistant.Infrastructure.Expectations.ExpectationStore>();
            services.AddScoped<Assistant.Application.Expectations.INonurgentDispatchStore, Assistant.Infrastructure.Expectations.NonurgentDispatchStore>();
        services.AddScoped<IFamilyOwnership, FamilyOwnership>(); services.AddSingleton<ITelegramClientFactory>(new Clients(telegram));
        services.AddScoped<IApprovalService, ApprovalService>(); services.AddSingleton<IRolePrompts>(new RolePrompts(typeof(RolePrompts).Assembly));
        services.RemoveAll<IGeneralAssistant>(); services.AddScoped<IGeneralAssistant>(_ => new NoopGeneral());
        services.RemoveAll<IHealthAssistant>(); services.AddScoped<IHealthAssistant>(_ => new NoopHealth()); services.AddScoped<IManagerUpdateHandler>(_ => new NoopManager());
        var config = new LlmConfig { Models = [new("codex-cli", "gpt-6.1-sol")], FastModels = [], CallsPerMinute = 1000,
            CallsPerDay = 10000, MaxContextMessages = 20, MaxInputChars = 32000, MaxOutputTokens = 4096,
            CallTimeoutSeconds = 5, MaxConcurrentCalls = 2, ModelCooldownMinutes = 1,
            Prices = new Dictionary<string, ModelPrice> { ["gpt-6.1-sol"] = new(0, 0) }, Budget = null };
        services.AddSingleton(config); services.AddSingleton(new ModelCatalog(config)); services.AddSingleton<IModelAvailability>(new ModelAvailability(Clock));
        services.AddSingleton<IChatClientProvider>(new ChatClientProvider(new Dictionary<string, IChatClient> { ["codex-cli"] = provider }));
        services.AddSingleton(new ConcurrentCallGate(2)); services.AddScoped<IBudgetGuard, BudgetGuard>();
        services.AddSingleton<IBudgetNoticeDispatcher>(new NoopBudget()); services.AddScoped<ILlmGateway, LlmGateway>();
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), provider, telegram, FamilyId);
    }

    private async Task Send(Runtime runtime, IncomingMessage incoming, long? exactUpdate = null)
    {
        using var scope = runtime.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UpdateHandler>().HandleAsync(Bot, runtime.Telegram,
            new(exactUpdate ?? ++update, incoming), Ct);
    }
    private Task Command(Runtime runtime, string text, long actor = 111, int topic = 7) => Send(runtime, Text(text, ++message, actor, topic));
    private async Task<Item> Upload(Runtime runtime, bool caption = false, bool album = false, int? imageIdentity = null, int? reading = null)
    {
        var id = ++logical; var file = "synthetic-image-" + id; var bytes = Png(imageIdentity ?? id);
        runtime.Telegram.Files[file] = bytes;
        var incoming = Text(caption ? "synthetic actual insulin caption" : "", ++message) with
        { Kind = MessageKind.Photo, Photo = new([new(file, "synthetic-unique-" + id, 32, 24, bytes.Length)]), MediaGroupId = album ? "synthetic-album" : null };
        if (caption) runtime.Images.Text.Enqueue(Caption());
        await Send(runtime, incoming);
        await using var s = Open(); var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.TelegramMessageId == incoming.MessageId, Ct);
        source.SourceMessageDbId.ShouldNotBeNull(); source.SourceAuthorUserId.ShouldBe(111);
        runtime.Images.Readings[source.CurrentInputRevisionId] = reading ?? id;
        return new(source.Id, source.CurrentInputRevisionId, source.BatchId!.Value, source.ItemNumber!.Value, reading ?? id, incoming.MessageId);
    }
    private async Task<IReadOnlyList<Item>> Collect(Runtime runtime, int count, bool caption = false, bool close = true)
    {
        await Command(runtime, "/photos_start"); var result = new List<Item>();
        for (var i = 0; i < count; i++) result.Add(await Upload(runtime, caption && i == 0, count == 40 && i >= 30));
        if (close) await Command(runtime, "/photos_close " + result[0].Batch.ToString("D"));
        return result;
    }
    private async Task Pass(Runtime runtime) => await ((VetPhotoBackgroundLoop)runtime.Provider.GetRequiredService<IVetPhotoBackgroundLoop>()).RunPassAsync(Bot, runtime.Telegram, Ct);
    private async Task Drain(Runtime runtime, int expectedImages, int maxPasses = 30)
    {
        for (var i = 0; i < maxPasses; i++)
        {
            await Pass(runtime); await using var s = Open();
            var terminal = await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image" && (a.State == "returned" || a.State == "failed" || a.State == "unknown"), Ct);
            if (terminal == expectedImages) return;
        }
        throw new InvalidOperationException("Synthetic bounded photo passes did not reach the expected terminal count.");
    }
    private async Task<VetPhotoReview> CurrentReview(Guid batch, bool complete = true)
    {
        await using var s = Open(); var b = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(x => x.Id == batch, Ct);
        return await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.BatchId == batch && r.BatchReviewRevision == b.ReviewRevision
            && (r.State == "preview" || r.State == "preview_failed") && r.CompletePreviewDelivered == complete, Ct);
    }
    private async Task<VetPhotoReview> VisibleReview(Runtime runtime, Guid batch)
    {
        var shown = runtime.Telegram.EditedButtons.Last(e => e.Buttons.Any(b => b.CallbackData.StartsWith("vp:a:", StringComparison.Ordinal)));
        var parts = shown.Buttons.Single(b => b.CallbackData.StartsWith("vp:a:", StringComparison.Ordinal)).CallbackData.Split(':');
        var id = Guid.ParseExact(parts[2], "N"); var revision = int.Parse(parts[3], CultureInfo.InvariantCulture);
        await using var state = Open(); var review = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == id, Ct);
        review.BatchId.ShouldBe(batch); review.Revision.ShouldBe(revision); review.State.ShouldBe("preview");
        review.CompletePreviewDelivered.ShouldBeTrue(); review.AcceptancePromptMessageId.ShouldBe(shown.Message);
        (await state.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(b => b.Id == batch, Ct)).ReviewRevision.ShouldBe(review.BatchReviewRevision.ShouldNotBeNull());
        return review;
    }
    private async Task Callback(Runtime runtime, VetPhotoReview review, long actor = 222, int topic = 7, int? prompt = null, string decision = "a")
    {
        using var scope = runtime.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UpdateHandler>().HandleAsync(Bot, runtime.Telegram,
            new(++update, null, new("synthetic-callback-" + update, actor, VetPhotoReviewComposer.Callback(decision, review),
                Scope.ChatId, prompt ?? review.AcceptancePromptMessageId ?? 9000, topic, "supergroup")), Ct);
    }
    private async Task Natural(Runtime runtime, object operation, object[]? events = null, int? reply = null, long actor = 222)
    {
        runtime.Images.Text.Enqueue(JsonSerializer.Serialize(new { needs_reply = false, events = events ?? [], unclear = Array.Empty<string>(), operation }, Json));
        await Send(runtime, Text("synthetic natural operation", ++message, actor) with { ReplyToMessageId = reply });
    }
    private async Task PhotoNatural(Runtime runtime, object operation, long actor = 111, int? reply = null)
    {
        var photo = JsonSerializer.SerializeToNode(operation, Json)!;
        var request = photo["kind"]!.GetValue<string>() switch
        {
            "correct" => "correct this synthetic photo",
            "duplicate" => "consider this synthetic photo a separate measurement",
            "accept" => "confirm this synthetic photo review",
            _ => throw new InvalidOperationException("Provide an explicit synthetic current action for this fixture.")
        };
        photo["action_evidence"] = request;
        runtime.Images.Text.Enqueue(JsonSerializer.Serialize(new { needs_reply = false, events = Array.Empty<object>(), unclear = Array.Empty<string>(), photo_operation = photo }, Json));
        await Send(runtime, Text(request, ++message, actor) with { ReplyToMessageId = reply });
    }
    private static object Glucose(string value, long? eventId = null, string? time = null) => new
    { type = "glucose", intent = "record", value, unit = "mmol/L", date = (string?)null, time, offset = (string?)null, time_evidence = "unknown", event_id = eventId };
    private static string Caption() => JsonSerializer.Serialize(new { needs_reply = false, unclear = Array.Empty<string>(),
        events = new object[] { new { type = "insulin", intent = "record", dose = "2.1250", unit = "U", product = "synthetic insulin",
            date = "2031-05-12", time = "09:00:00", offset = "+00:00", time_evidence = "explicit" },
            new { type = "glucose", intent = "record", value = "5.0100", unit = "mmol/L", date = "2031-05-12", time = "10:01:05", offset = "+00:00", time_evidence = "explicit" } },
        photo_caption = new { intent = "record", value = (string?)null, unit = (string?)null, year = (int?)null, month = (int?)null, day = (int?)null, time = (string?)null, offset = (string?)null } }, Json);
    private static async Task<string> Snapshot<T>(IQueryable<T> rows) => JsonSerializer.Serialize(await rows.ToListAsync(Ct), Json);
    private async Task<string> Protected(VetTestSession s) => JsonSerializer.Serialize(new
    {
        Facts = await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().OrderBy(x => x.Id)),
        Actions = await Snapshot(s.Context.VetDiaryActions.AsNoTracking().OrderBy(x => x.Id)),
        Candidates = await Snapshot(s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id)),
        References = await Snapshot(s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x => x.Id)),
        Blobs = await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x => x.Id))
    }, Json);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Capacity_full_archive_backlog_cannot_starve_an_already_retained_image_in_one_background_pass(bool retainedFirst)
    {
        await SeedAsync();
        var original = Png(1);
        var capacity = new VetPhotoCapacity(MaxBytes: original.LongLength);
        await using var runtime = Build(capacity: capacity);
        await Command(runtime, "/photos_start");
        Item retained;
        var blocked = new List<Item>();
        async Task RetainAsync(Item item)
        {
            using var request = runtime.Provider.CreateScope();
            request.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(FamilyId);
            var archived = await request.ServiceProvider.GetRequiredService<VetPhotoProcessor>()
                .ProcessAsync(new(Scope, item.Source, item.Input, 111) { ArchiveOnly = true }, runtime.Telegram, Ct);
            archived.Status.ShouldBe(VetPhotoProcessStatus.Deferred);
            archived.Category.ShouldBe("archive_retained");
            var before = await request.ServiceProvider.GetRequiredService<IVetPhotoArchiveStore>().GetCapacityAsync(Scope, 111, Ct);
            before.RetainedBytes.ShouldBe(original.LongLength);
            before.RetainedInputs.ShouldBe(1L);
            before.RetainedResults.ShouldBe(0L);
            before.ReservedBytes.ShouldBe(0L);
            before.ReservedInputs.ShouldBe(0L);
            before.ReservedResults.ShouldBe(0L);
        }
        if (retainedFirst)
        {
            retained = await Upload(runtime, imageIdentity: 1, reading: 1);
            await RetainAsync(retained);
            Clock.UtcNow = Now.AddSeconds(1);
            for (var index = 0; index < 5; index++) blocked.Add(await Upload(runtime, imageIdentity: 101 + index, reading: 101 + index));
        }
        else
        {
            for (var index = 0; index < 5; index++) blocked.Add(await Upload(runtime, imageIdentity: 101 + index, reading: 101 + index));
            Clock.UtcNow = Now.AddSeconds(1);
            retained = await Upload(runtime, imageIdentity: 1, reading: 1);
            await RetainAsync(retained);
        }
        retained.Reading.ShouldBe(1);
        var retainedFile = retainedFirst ? "synthetic-image-1" : "synthetic-image-6";
        runtime.Images.ImageRequests.Count.ShouldBe(0);
        runtime.Telegram.DownloadedFiles.ToArray().ShouldBe(new[] { retainedFile });
        blocked.Select(item => item.Batch).ShouldAllBe(batch => batch == retained.Batch);
        await Command(runtime, "/photos_close " + retained.Batch.ToString("D"));
        var blockedSources = blocked.Select(item => item.Source).ToArray();
        var blockedInputs = blocked.Select(item => item.Input).ToArray();
        using (var request = runtime.Provider.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(FamilyId);
            var queued = await request.ServiceProvider.GetRequiredService<IVetPhotoArchiveDispatchStore>()
                .GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
            queued.Count.ShouldBe(5);
            queued.Select(work => work.SourceId).Order().ShouldBe(blockedSources.Order());
            queued.All(work => work.ArchiveOnly && work.Scope == Scope).ShouldBeTrue();
        }
        await using (var before = Open())
        {
            (await before.Context.Set<VetPhotoSource>().CountAsync(Ct)).ShouldBe(6);
            (await before.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1);
            (await before.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("closed");
            (await before.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
            (await before.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        }

        await Pass(runtime);
        runtime.Images.ImageRequests.Count.ShouldBe(1);
        var sent = runtime.Images.ImageRequests.Single();
        sent.Source.ShouldBe(retained.Source);
        sent.Input.ShouldBe(retained.Input);
        sent.Model.ShouldBe("gpt-6.1-sol");
        sent.Bytes.ShouldBe(original.Length);
        runtime.Telegram.DownloadedFiles.ToArray().ShouldBe(new[] { retainedFile });
        await using (var state = Open())
        {
            var extraction = await state.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(Ct);
            extraction.SourceId.ShouldBe(retained.Source);
            extraction.InputRevisionId.ShouldBe(retained.Input);
            var attempt = await state.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image", Ct);
            attempt.SourceId.ShouldBe(retained.Source);
            attempt.InputRevisionId.ShouldBe(retained.Input);
            attempt.State.ShouldBe("returned");
            attempt.ReservedResultSlot.ShouldBeFalse();
            var call = await state.Context.LlmCalls.AsNoTracking().SingleAsync(c => c.AttemptKey != null, Ct);
            call.AttemptKey.ShouldBe(attempt.Id);
            call.Provider.ShouldBe("codex-cli");
            call.Model.ShouldBe("gpt-6.1-sol");
            call.Outcome.ShouldBe(LlmCallOutcome.Ok);
            call.Cost.ShouldBe(0m);
            var source = await state.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(s => s.Id == retained.Source, Ct);
            call.TriggerMessageId.ShouldBe(source.SourceMessageDbId);
            (await state.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(Ct)).InputRevisionId.ShouldBe(retained.Input);
            (await state.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync(Ct)).Content.ShouldBe(original);
            (await state.Context.Set<VetPhotoOriginalReference>().CountAsync(r => blockedInputs.Contains(r.InputRevisionId), Ct)).ShouldBe(0);
            (await state.Context.Set<VetPhotoExtraction>().CountAsync(e => blockedInputs.Contains(e.InputRevisionId), Ct)).ShouldBe(0);
            var sources = await state.Context.Set<VetPhotoSource>().AsNoTracking().Where(s => blockedSources.Contains(s.Id)).ToListAsync(Ct);
            sources.Count.ShouldBe(5);
            foreach (var item in blocked)
            {
                var metadata = sources.Single(s => s.Id == item.Source);
                metadata.SourceMessageDbId.ShouldNotBeNull();
                metadata.CurrentInputRevisionId.ShouldBe(item.Input);
                metadata.BatchId.ShouldBe(retained.Batch);
                metadata.State.ShouldBe("admitted");
                var download = await state.Context.Set<VetPhotoAttempt>().AsNoTracking()
                    .SingleAsync(a => a.InputRevisionId == item.Input && a.Kind == "download", Ct);
                download.State.ShouldBe("queued");
                download.DownloadAttemptCount.ShouldBe(0);
                download.ReservedBytes.ShouldBe(0L);
                download.ReservedInputSlot.ShouldBeFalse();
                download.ReservedResultSlot.ShouldBeFalse();
            }
            (await state.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image" && blockedInputs.Contains(a.InputRevisionId), Ct)).ShouldBe(0);
            (await state.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        }

        await Command(runtime, "/photos_save " + retained.Batch.ToString("D"));
        var review = await VisibleReview(runtime, retained.Batch);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        pages.Length.ShouldBe(review.PageCount);
        deliveries.Length.ShouldBe(pages.Length);
        pages.All(page => page.Length <= 3500).ShouldBeTrue();
        foreach (var page in pages)
            runtime.Telegram.SentMessages.Any(message => message.ChatId == Scope.ChatId && message.TopicId == Scope.TopicId && message.Text == page).ShouldBeTrue();
        var shown = string.Join("\n", pages);
        foreach (var item in blocked.Append(retained)) shown.ShouldContain("#" + item.Number);
        var selection = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Single();
        selection.SourceId.ShouldBe(retained.Source);
        selection.InputRevisionId.ShouldBe(retained.Input);
        selection.Disposition.ShouldBe("save");
        await Callback(runtime, review);
        await using var saved = Open();
        var fact = await saved.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct);
        fact.PhotoSourceId.ShouldBe(retained.Source);
        fact.InputRevisionId.ShouldBe(retained.Input);
        fact.Value.ShouldBe(5.01m);
        fact.Unit.ShouldBe("mmol/L");
        fact.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-12T10:01:05Z"));
        fact.SourceAuthorUserId.ShouldBe(111);
        var action = await saved.Context.VetDiaryActions.AsNoTracking().SingleAsync(Ct);
        action.Kind.ShouldBe("photo_save");
        action.ActorUserId.ShouldBe(222);
        var totals = await new VetPhotoStore(saved.Context, saved.Current, Clock, capacity, new VetPhotoImageDecoder()).GetCapacityAsync(Scope, 111, Ct);
        totals.RetainedBytes.ShouldBe(original.LongLength);
        totals.RetainedInputs.ShouldBe(1L);
        totals.RetainedResults.ShouldBe(1L);
        totals.ReservedBytes.ShouldBe(0L);
        totals.ReservedInputs.ShouldBe(0L);
        totals.ReservedResults.ShouldBe(0L);
        var factsBeforeReplay = await Snapshot(saved.Context.Set<VetEvent>().AsNoTracking());
        await Callback(runtime, review);
        await Pass(runtime);
        (await Snapshot(saved.Context.Set<VetEvent>().AsNoTracking())).ShouldBe(factsBeforeReplay);
        (await saved.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await saved.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(1);
        (await saved.Context.Set<VetPhotoSource>().CountAsync(s => blockedSources.Contains(s.Id), Ct)).ShouldBe(5);
        runtime.Images.ImageRequests.Count.ShouldBe(1);
        runtime.Telegram.DownloadedFiles.ToArray().ShouldBe(new[] { retainedFile });
        runtime.Telegram.ClearSent();
        await Command(runtime, "/today", 222);
        string.Join("\n", runtime.Telegram.SentMessages.Select(message => message.Text)).ShouldContain("#" + fact.Id);
    }

    [Fact]
    public async Task Forty_sources_through_update_handler_background_gateway_complete_pages_callback_and_history_preserve_every_exact_fact()
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 40, caption: true);
        await using (var before = Open())
        {
            (await before.Context.Set<VetPhotoSource>().CountAsync(Ct)).ShouldBe(40);
            (await before.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1);
            (await before.Context.Set<VetEvent>().CountAsync(e => e.SourceKind == "photo", Ct)).ShouldBe(0);
            var insulin = await before.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct); insulin.EventType.ShouldBe("insulin"); insulin.Value.ShouldBe(2.1250m);
            insulin.SourceKind.ShouldBe("text"); insulin.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-12T09:00:00Z"));
            (await before.Context.Set<VetExtractionResult>().AsNoTracking().SingleAsync(Ct)).Json.ShouldContain("5.0100");
        }
        await Drain(runtime, 40); var review = await CurrentReview(items[0].Batch); review.PageCount.ShouldBeGreaterThan(1);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        pages.Length.ShouldBe(review.PageCount); deliveries.Length.ShouldBe(pages.Length); pages.All(p => p.Length <= 3500).ShouldBeTrue();
        for (var i = 0; i < pages.Length; i++)
        {
            var delivery = deliveries.Single(d => d.PageIndex == i); delivery.MessageId.ShouldBeGreaterThan(0);
            delivery.TextHash.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pages[i]))).ToLowerInvariant());
            runtime.Telegram.SentMessages.Any(m => m.Text == pages[i] && m.ChatId == Scope.ChatId && m.TopicId == Scope.TopicId).ShouldBeTrue();
        }
        review.AcceptancePromptMessageId.ShouldBe(deliveries.Single(d => d.PageIndex == pages.Length - 1).MessageId);
        var selection = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        selection.Length.ShouldBe(40); selection.Select(x => x.SourceId).Order().ShouldBe(items.Select(x => x.Source).Order());
        selection.All(x => x.Disposition == "save").ShouldBeTrue();
        await using (var unsaved = Open()) (await unsaved.Context.Set<VetEvent>().CountAsync(e => e.SourceKind == "photo", Ct)).ShouldBe(0);
        await Callback(runtime, review);
        await using var s = Open(); var facts = await s.Context.Set<VetEvent>().AsNoTracking().Where(e => e.SourceKind == "photo").ToListAsync(Ct);
        facts.Count.ShouldBe(40); facts.Select(e => e.Id).Distinct().Count().ShouldBe(40);
        foreach (var item in items)
        {
            var fact = facts.Single(e => e.PhotoSourceId == item.Source); fact.Value.ShouldBe(Value(item.Reading)); fact.Unit.ShouldBe("mmol/L"); fact.OccurredAt.ShouldBe(At(item.Reading));
            fact.SourceAuthorUserId.ShouldBe(111); fact.InputRevisionId.ShouldBe(item.Input); fact.TelegramMessageId.ShouldBe(item.Message); fact.Revision.ShouldBe(1);
            var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == item.Source, Ct); fact.SourceMessageDbId.ShouldBe(source.SourceMessageDbId!.Value);
            var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == item.Source, Ct); candidate.EventId.ShouldBe(fact.Id); candidate.EventRevision.ShouldBe(1); candidate.State.ShouldBe("saved");
            var extraction = await s.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(e => e.Id == fact.ExtractionResultId, Ct); extraction.SourceId.ShouldBe(item.Source); extraction.InputRevisionId.ShouldBe(item.Input);
        }
        var photoAction = await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.ActorUserId == 222, Ct); photoAction.Kind.ShouldBe("photo_save");
        (await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("completed");
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(40);
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null, Ct)).ShouldBe(40);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image" && a.State == "returned" && !a.ReservedResultSlot, Ct)).ShouldBe(40);
        var calls = await s.Context.LlmCalls.AsNoTracking().Where(c => c.AttemptKey != null).ToListAsync(Ct); calls.Count.ShouldBe(40);
        calls.All(c => c.Model == "gpt-6.1-sol" && c.Provider == "codex-cli" && c.Outcome == LlmCallOutcome.Ok && c.ChatId == Scope.ChatId && c.TopicId == Scope.TopicId && c.TriggerMessageId != null).ShouldBeTrue();
        calls.Select(c => c.AttemptKey).Order().ShouldBe((await s.Context.Set<VetPhotoAttempt>().AsNoTracking().Where(a => a.Kind == "image").Select(a => (Guid?)a.Id).ToListAsync(Ct)).Order());
        runtime.Images.ImageRequests.Count.ShouldBe(40); runtime.Images.ImageRequests.All(r => r.Model == "gpt-6.1-sol" && r.Bytes > 0).ShouldBeTrue();
        var capacity = await new VetPhotoStore(s.Context, s.Current, Clock, new(), new VetPhotoImageDecoder()).GetCapacityAsync(Scope, 111, Ct);
        capacity.ReservedResults.ShouldBe(0L); capacity.ReservedBytes.ShouldBe(0L);
        var unchanged = await Protected(s); await Callback(runtime, review); await Pass(runtime); (await Protected(s)).ShouldBe(unchanged); runtime.Images.ImageRequests.Count.ShouldBe(40);
        runtime.Telegram.ClearSent(); await Command(runtime, "/today", 222); await Command(runtime, "/more", 222); await Command(runtime, "/more", 222); await Command(runtime, "/photos", 222);
        var history = string.Join("\n", runtime.Telegram.SentMessages.Select(m => m.Text)); foreach (var fact in facts) history.ShouldContain("#" + fact.Id);
        history.ShouldContain("completed"); (await Protected(s)).ShouldBe(unchanged);
    }
    [Fact]
    public async Task Fifty_first_source_is_visible_rejected_metadata_and_never_archived_dispatched_or_saved()
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 50, close: false);
        var bytes = Png(51); runtime.Telegram.Files["synthetic-overflow"] = bytes;
        var overflow = Text("", ++message) with { Kind = MessageKind.Photo, Photo = new([new("synthetic-overflow", "synthetic-overflow-unique", 32, 24, bytes.Length)]) };
        await Send(runtime, overflow); await using var s = Open();
        var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.TelegramMessageId == overflow.MessageId, Ct); source.State.ShouldBe("full"); source.ItemNumber.ShouldBeNull();
        var rejected = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == source.Id, Ct);
        rejected.State.ShouldBe("full"); rejected.BatchId.ShouldBeNull(); rejected.EventId.ShouldBeNull();
        rejected.SourceId.ShouldBe(source.Id); rejected.InputRevisionId.ShouldBeNull();
        source.CurrentInputRevisionId.ShouldNotBe(Guid.Empty);
        rejected.ExtractionResultId.ShouldBeNull(); rejected.CandidateOrdinal.ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.SourceId == source.Id, Ct)).ShouldBe(0);
        await Command(runtime, "/photos_close " + items[0].Batch.ToString("D")); await Drain(runtime, 50);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.InputRevisionId == source.CurrentInputRevisionId, Ct)).ShouldBe(0);
        runtime.Images.ImageRequests.Count.ShouldBe(50); runtime.Telegram.DownloadedFiles.ShouldNotContain("synthetic-overflow");
        string.Join("\n", runtime.Telegram.SentMessages.Select(m => m.Text)).ShouldContain("50 фото");
        var review = await CurrentReview(items[0].Batch); JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Length.ShouldBe(50);
        (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Six_inert_closed_batches_cannot_hide_a_later_actual_returned_batch_complete_preview()
    {
        await SeedAsync(); await using var runtime = Build(); var old = new List<Guid>();
        for (var i = 1; i <= 6; i++) { var one = await Collect(runtime, 1); await Drain(runtime, i); old.Add(one[0].Batch); (await CurrentReview(one[0].Batch)).CompletePreviewDelivered.ShouldBeTrue(); }
        var later = await Collect(runtime, 1); await Drain(runtime, 7);
        var current = await CurrentReview(later[0].Batch); current.CompletePreviewDelivered.ShouldBeTrue();
        JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(current.SelectionJson, Json)!.Single().SourceId.ShouldBe(later[0].Source);
        await using var s = Open(); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0); old.ShouldNotContain(current.BatchId!.Value);
    }
    [Theory]
    [InlineData("member")][InlineData("place")][InlineData("topic")][InlineData("prompt")][InlineData("revision")]
    public async Task Callback_rechecks_exact_current_actor_place_prompt_and_revision_without_any_write(string guard)
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 1); await Drain(runtime, 1); var review = await CurrentReview(items[0].Batch);
        await using var s = Open(); var before = await Protected(s);
        if (guard == "member") await s.Context.FamilyMembers.Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (guard == "place") await s.Context.Places.Where(p => p.BotId == Bot.BotDbId && p.ChatId == Scope.ChatId && p.TopicId == Scope.TopicId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        if (guard == "revision") review.Revision++;
        await Callback(runtime, review, topic: guard == "topic" ? 8 : 7, prompt: guard == "prompt" ? review.AcceptancePromptMessageId + 1 : null);
        (await Protected(s)).ShouldBe(before); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        runtime.Telegram.AnsweredCallbacks.Count.ShouldBeGreaterThan(0);
    }
    [Fact]
    public async Task Partial_preview_failure_and_unseen_callback_are_zero_write_then_restart_delivers_complete_current_proof()
    {
        await SeedAsync(); var telegram = new ReviewTelegram { FailAfterFirstReviewPage = true }; var images = new ImageProvider();
        await using (var runtime = Build(images, telegram))
        {
            var items = await Collect(runtime, 40); await Drain(runtime, 40); var failed = await CurrentReview(items[0].Batch, complete: false);
            failed.PageCount.ShouldBeGreaterThan(1); JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(failed.DeliveredPagesJson, Json)!.Length.ShouldBe(1);
            failed.AcceptancePromptMessageId.ShouldBeNull(); await using var s = Open(); var before = await Protected(s);
            await Callback(runtime, failed, prompt: 9999); (await Protected(s)).ShouldBe(before);
            telegram.FailAfterFirstReviewPage = false; await using var restart = Build(images, telegram);
            var priorSends = telegram.SentMessages.Count; await Pass(restart); telegram.SentMessages.Count.ShouldBe(priorSends);
            await Command(restart, "/photos_review " + items[0].Batch.ToString("D"));
            var delivered = await CurrentReview(items[0].Batch); delivered.CompletePreviewDelivered.ShouldBeTrue();
            JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(delivered.DeliveredPagesJson, Json)!.Length.ShouldBe(delivered.PageCount);
            images.ImageRequests.Count.ShouldBe(40); (await Protected(s)).ShouldBe(before);
        }
    }
    [Fact]
    public async Task Cancel_keeps_admitted_archive_work_and_insulin_but_stops_every_queued_image_across_fresh_background_scopes()
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 3, caption: true, close: false);
        await Command(runtime, "/photos_cancel " + items[0].Batch.ToString("D"));
        await using (var proposed = Open())
        {
            (await proposed.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("closed");
            (await proposed.Context.Set<VetPhotoCandidate>().CountAsync(c => c.State == "cancelled", Ct)).ShouldBe(0);
            (await proposed.Context.VetDiaryActions.CountAsync(a => a.Kind != "save", Ct)).ShouldBe(0);
        }
        var cancellation = await CurrentReview(items[0].Batch); cancellation.CompletePreviewDelivered.ShouldBeTrue();
        await Callback(runtime, cancellation);
        for (var i = 0; i < 3; i++) await Pass(runtime);
        await using var s = Open(); (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(3);
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null, Ct)).ShouldBe(3); runtime.Images.ImageRequests.ShouldBeEmpty();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0);
        var insulin = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(Ct); insulin.EventType.ShouldBe("insulin"); insulin.Value.ShouldBe(2.1250m);
        (await s.Context.Set<VetPhotoCandidate>().CountAsync(c => c.State == "cancelled", Ct)).ShouldBe(3);
        (await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("cancelled");
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Identical_originals_link_one_canonical_while_equal_distinct_originals_require_explicit_separate(bool identical)
    {
        await SeedAsync(); await using var runtime = Build(); await Command(runtime, "/photos_start"); var first = await Upload(runtime);
        var second = await Upload(runtime, imageIdentity: identical ? first.Reading : null, reading: first.Reading);
        await Command(runtime, "/photos_close " + first.Batch.ToString("D")); await Drain(runtime, 2); await using var s = Open();
        if (!identical)
        {
            (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
            (await s.Context.Set<VetPhotoReview>().CountAsync(r => r.BatchId == first.Batch && r.Kind != "evidence" && r.State == "preview" && r.CompletePreviewDelivered, Ct)).ShouldBe(0);
            foreach (var exactSource in new[] { first.Source, second.Source })
            {
                var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == exactSource, Ct);
                await PhotoNatural(runtime, new { kind = "duplicate", batch_id = first.Batch, candidate_ids = new[] { candidate.Id }, duplicate_choice = "separate" });
            }
            await Command(runtime, "/photos_review " + first.Batch.ToString("D"));
        }
        var review = identical ? await CurrentReview(first.Batch) : await VisibleReview(runtime, first.Batch);
        JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Length.ShouldBe(2);
        await Callback(runtime, review);
        var facts = await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(Ct); facts.Count.ShouldBe(identical ? 1 : 2);
        if (identical)
        {
            var linked = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.State == "linked", Ct); linked.EventId.ShouldBeNull(); linked.DuplicateEventId.ShouldBe(facts.Single().Id); linked.DuplicateEventRevision.ShouldBe(facts.Single().Revision);
            (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null, Ct)).ShouldBe(1);
        }
        else (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null, Ct)).ShouldBe(2);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(2);
    }
    [Theory]
    [InlineData("unique")][InlineData("ambiguous")][InlineData("text_reply")][InlineData("photo_reply")][InlineData("incomplete")]
    public async Task Natural_confirmation_routes_only_the_exact_shown_text_or_photo_proposal(string target)
    {
        await SeedAsync(); var telegram = new ReviewTelegram { FailEveryReviewPage = target == "incomplete" }; await using var runtime = Build(telegram: telegram);
        var items = await Collect(runtime, 1); await Drain(runtime, 1); VetPhotoReview? photo = target == "incomplete" ? null : await CurrentReview(items[0].Batch);
        VetPendingDecision? text = null;
        if (target is "ambiguous" or "text_reply" or "photo_reply")
        {
            runtime.Images.Text.Enqueue(JsonSerializer.Serialize(new { needs_reply = false, events = new[] { Glucose("8.2500") }, unclear = Array.Empty<string>() }, Json));
            await Send(runtime, Text("synthetic incomplete timestamp", ++message)); await using var pending = Open(); text = await pending.Context.Set<VetPendingDecision>().AsNoTracking().SingleAsync(p => p.State == "pending", Ct);
        }
        await using var s = Open(); var before = await Protected(s);
        await Natural(runtime, new { kind = "accept" }, reply: target == "text_reply" ? text!.PromptMessageId : target == "photo_reply" ? photo!.AcceptancePromptMessageId : null);
        var facts = await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(Ct);
        if (target is "unique" or "photo_reply") { facts.Count.ShouldBe(1); facts.Single().SourceKind.ShouldBe("photo"); facts.Single().PhotoSourceId.ShouldBe(items[0].Source); }
        else if (target == "text_reply")
        { facts.ShouldBeEmpty(); (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == photo!.Id, Ct)).State.ShouldBe("preview");
          (await s.Context.Set<VetPendingDecision>().AsNoTracking().SingleAsync(p => p.Id == text!.Id, Ct)).State.ShouldBe("pending"); }
        else { facts.ShouldBeEmpty(); (await Protected(s)).ShouldBe(before); }
        if (target == "ambiguous") string.Join("\n", runtime.Telegram.SentMessages.Select(m => m.Text)).ShouldContain("несколько предложений");
    }
    [Theory]
    [InlineData("correct")][InlineData("delete")][InlineData("del")]
    public async Task Diary_photo_operations_propose_then_confirm_atomically_and_undo_preserves_bound_caption_insulin_and_original_bytes(string kind)
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 1, caption: true); await Drain(runtime, 1); await Callback(runtime, await CurrentReview(items[0].Batch));
        await using var s = Open(); var original = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(e => e.SourceKind == "photo", Ct);
        var insulin = await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().Where(e => e.EventType == "insulin")); var blobs = await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking());
        if (kind == "del") await Command(runtime, "/del " + original.Id.ToString(CultureInfo.InvariantCulture), 222);
        else await Natural(runtime, new { kind, event_id = original.Id }, kind == "correct" ? [Glucose("7.8750", original.Id)] : [], actor: 222);
        (await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(e => e.Id == original.Id, Ct)).Value.ShouldBe(original.Value);
        var review = await CurrentReview(items[0].Batch); review.CompletePreviewDelivered.ShouldBeTrue(); await Callback(runtime, review);
        var changed = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(e => e.Id == original.Id, Ct); changed.Revision.ShouldBe(original.Revision + 1); changed.Id.ShouldBe(original.Id);
        if (kind == "correct") { changed.Value.ShouldBe(7.8750m); changed.OccurredAt.ShouldBe(original.OccurredAt); changed.InputRevisionId.ShouldBe(original.InputRevisionId); }
        else changed.DeletedAt.ShouldNotBeNull();
        (await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().Where(e => e.EventType == "insulin"))).ShouldBe(insulin); (await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking())).ShouldBe(blobs);
        await Command(runtime, "/undo", 222); var restored = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync(e => e.Id == original.Id, Ct);
        restored.Value.ShouldBe(original.Value); restored.DeletedAt.ShouldBeNull(); restored.Revision.ShouldBe(changed.Revision + 1);
        (await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().Where(e => e.EventType == "insulin"))).ShouldBe(insulin); (await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking())).ShouldBe(blobs);
        runtime.Images.ImageRequests.Count.ShouldBe(1);
    }
    [Fact]
    public async Task Scheduled_partial_unknown_window_applies_only_the_reviewed_returned_correction_and_preserves_unknown_fact_charge()
    {
        await SeedAsync(); await using var runtime = Build(); var items = await Collect(runtime, 2); await Drain(runtime, 2); await Callback(runtime, await CurrentReview(items[0].Batch));
        var proposed = new Dictionary<Guid, (decimal Value, DateTimeOffset At)>
        {
            [items[0].Source] = (6.0100m, DateTimeOffset.Parse("2031-05-12T11:41:05Z")),
            [items[1].Source] = (6.0200m, DateTimeOffset.Parse("2031-05-12T11:42:05Z"))
        };
        runtime.Images.Readings[items[0].Input] = 101; runtime.Images.Readings[items[1].Input] = 102;
        await using var state = Open();
        var originals = await state.Context.Set<VetEvent>().AsNoTracking().OrderBy(e => e.Id).ToArrayAsync(Ct); originals.Length.ShouldBe(2);
        var before = JsonSerializer.Serialize(originals, Json); var actionCount = await state.Context.VetDiaryActions.CountAsync(Ct);
        await Command(runtime, "/photos_reprocess current " + items[0].Batch.ToString("D"));
        var run = await state.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct); var initial = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == run.SelectionReviewId, Ct);
        await Callback(runtime, initial, 111); runtime.Images.UnknownCallNumber = 4;
        await Command(runtime, "/photos_continue " + run.Id.ToString("D")); await Drain(runtime, 4);
        var window = await state.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(w => w.RunId == run.Id, Ct);
        var returned = await state.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.RunWindowId == window.Id && a.State == "returned", Ct);
        var unknown = await state.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.RunWindowId == window.Id && a.State == "unknown", Ct);
        unknown.ReservedResultSlot.ShouldBeTrue(); unknown.ExtractionResultId.ShouldBeNull(); returned.ReservedResultSlot.ShouldBeFalse();
        var comparison = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == window.ComparisonReviewId, Ct); comparison.CompletePreviewDelivered.ShouldBeTrue();
        var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(comparison.SelectionJson, Json)!; selected.Length.ShouldBe(2);
        var correction = selected.Single(x => x.SourceId == returned.SourceId); correction.Disposition.ShouldBe("correct");
        correction.State.ShouldNotBeNull().Value.ShouldBe(proposed[returned.SourceId].Value); correction.State.OccurredAt.ShouldBe(proposed[returned.SourceId].At);
        selected.Single(x => x.SourceId == unknown.SourceId).Disposition.ShouldBe("keep");
        (await Snapshot(state.Context.Set<VetEvent>().AsNoTracking().OrderBy(e => e.Id))).ShouldBe(before);
        (await state.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actionCount);
        await Callback(runtime, comparison);
        var after = await state.Context.Set<VetEvent>().AsNoTracking().OrderBy(e => e.Id).ToArrayAsync(Ct); after.Length.ShouldBe(2);
        var corrected = after.Single(e => e.PhotoSourceId == returned.SourceId); var prior = originals.Single(e => e.PhotoSourceId == returned.SourceId);
        corrected.Id.ShouldBe(prior.Id); corrected.Revision.ShouldBe(prior.Revision + 1); corrected.Value.ShouldBe(proposed[returned.SourceId].Value);
        corrected.OccurredAt.ShouldBe(proposed[returned.SourceId].At); corrected.Unit.ShouldBe("mmol/L"); corrected.SourceAuthorUserId.ShouldBe(111);
        corrected.InputRevisionId.ShouldBe(prior.InputRevisionId); corrected.ExtractionResultId.ShouldBe(returned.ExtractionResultId.ShouldNotBeNull()); corrected.PhotoSourceId.ShouldBe(prior.PhotoSourceId);
        JsonSerializer.Serialize(after.Single(e => e.PhotoSourceId == unknown.SourceId), Json).ShouldBe(JsonSerializer.Serialize(originals.Single(e => e.PhotoSourceId == unknown.SourceId), Json));
        (await state.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actionCount + 1);
        var accepted = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == comparison.Id, Ct); accepted.State.ShouldBe("accepted"); accepted.DecisionActorUserId.ShouldBe(222);
        var action = await state.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == accepted.ActionId, Ct); action.ActorUserId.ShouldBe(222); action.ChatId.ShouldBe(Scope.ChatId); action.TopicId.ShouldBe(Scope.TopicId);
        (await state.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct)).State.ShouldBe("completed");
        JsonSerializer.Serialize(await state.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == unknown.Id, Ct), Json).ShouldBe(JsonSerializer.Serialize(unknown, Json));
        (await new VetPhotoStore(state.Context, state.Current, Clock, new(), new VetPhotoImageDecoder()).GetCapacityAsync(Scope, 111, Ct)).ReservedResults.ShouldBe(1L);
        await Pass(runtime); await Pass(runtime); runtime.Images.ImageRequests.Count.ShouldBe(4);
        (await state.Context.LlmCalls.CountAsync(c => c.AttemptKey != null, Ct)).ShouldBe(4);
    }
    [Fact]
    public async Task Precancelled_background_pass_and_foreign_receiving_family_never_dispatch_or_write_a_photo_fact()
    {
        await SeedAsync(); await using var runtime = Build(); await Collect(runtime, 1); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => ((VetPhotoBackgroundLoop)runtime.Provider.GetRequiredService<IVetPhotoBackgroundLoop>()).RunPassAsync(Bot, runtime.Telegram, cancelled.Token));
        runtime.Images.ImageRequests.ShouldBeEmpty(); await using var s = Open(); (await s.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        var foreignFamily = new Family { Name = "synthetic second family", CreatedAt = Now };
        s.Context.Add(foreignFamily); await s.Context.SaveChangesAsync(Ct);
        s.Context.Add(new FamilyMember { FamilyId = foreignFamily.Id, TelegramUserId = 111, DisplayName = "synthetic approved member", Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        await s.Context.SaveChangesAsync(Ct);
        using var scope = runtime.Provider.CreateScope(); var foreign = Bot with { FamilyId = foreignFamily.Id };
        await Should.ThrowAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<UpdateHandler>().HandleAsync(foreign, runtime.Telegram, new(++update, Text("/photos_start", ++message)), Ct));
        (await s.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1); runtime.Images.ImageRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("callback_accept")][InlineData("callback_decline")][InlineData("photo_natural")][InlineData("normal_natural")]
    public async Task Fully_delivered_evidence_is_visible_once_and_cannot_authorize_any_fact_or_action(string decision)
    {
        await SeedAsync(); var images = new ImageProvider(); images.MissingClockReadings.Add(1);
        await using var runtime = Build(images); var item = (await Collect(runtime, 1)).Single(); await Drain(runtime, 1);
        var notice = await CurrentReview(item.Batch); notice.Kind.ShouldBe("evidence"); notice.CompletePreviewDelivered.ShouldBeTrue();
        var shown = runtime.Telegram.SentMessages.Where(m => m.Text.Contains(item.Source.ToString("D"), StringComparison.Ordinal)).ToArray();
        shown.ShouldNotBeEmpty(); string.Join("\n", shown.Select(m => m.Text)).ShouldContain("5.0100");
        var selection = JsonSerializer.Deserialize<VetPhotoEvidenceSelection[]>(notice.SelectionJson, Json)!.Single();
        selection.SourceId.ShouldBe(item.Source); selection.CurrentInputId.ShouldBe(item.Input);
        await using (var actual = Open())
        {
            var candidate = await actual.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == item.Source, Ct);
            selection.CandidateId.ShouldBe(candidate.Id); selection.CandidateRevision.ShouldBe(candidate.Revision);
            selection.CandidateInputId.ShouldBe(candidate.InputRevisionId); selection.CandidateExtractionId.ShouldBe(candidate.ExtractionResultId);
            selection.ShownExtractionId.ShouldBe((await actual.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(Ct)).Id);
        }
        runtime.Telegram.EditedButtons.Where(e => e.Buttons.Count > 0).ShouldBeEmpty();
        await using var state = Open(); var before = await Protected(state); var sent = runtime.Telegram.SentMessages.Count;
        var notices = await state.Context.Set<VetPhotoReview>().AsNoTracking().Where(r => r.Kind == "evidence").ToArrayAsync(Ct);
        notices.Length.ShouldBe(2);
        var historical = notices.Single(r => r.Id != notice.Id); historical.State.ShouldBe("stale");
        historical.BatchId.ShouldBe(item.Batch); historical.BatchReviewRevision.ShouldNotBeNull().ShouldBeLessThan(notice.BatchReviewRevision.ShouldNotBeNull());
        JsonSerializer.Deserialize<VetPhotoEvidenceSelection[]>(historical.SelectionJson, Json)!.Single().ShownExtractionId.ShouldBeNull();
        var batch = await state.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(b => b.Id == item.Batch, Ct);
        var profile = await state.Context.Set<VetProfile>().AsNoTracking().SingleAsync(Ct);
        var current = notices.Single(r => r.State == "preview" && r.BatchId == batch.Id && r.BatchReviewRevision == batch.ReviewRevision
            && r.ProfileId == profile.Id && r.ProfileRevision == profile.Revision);
        current.Id.ShouldBe(notice.Id); current.CompletePreviewDelivered.ShouldBeTrue();
        JsonSerializer.Deserialize<VetPhotoEvidenceSelection[]>(current.SelectionJson, Json)!.Single().ShownExtractionId.ShouldBe(selection.ShownExtractionId);
        await Pass(runtime); await Pass(runtime); runtime.Telegram.SentMessages.Count.ShouldBe(sent); runtime.Images.ImageRequests.Count.ShouldBe(1);
        if (decision == "callback_accept") await Callback(runtime, notice);
        else if (decision == "callback_decline") await Callback(runtime, notice, decision: "d");
        else if (decision == "photo_natural") await PhotoNatural(runtime, new { kind = "accept", review_id = notice.Id, review_revision = notice.Revision }, actor: 222);
        else await Natural(runtime, new { kind = "accept" }, reply: notice.AcceptancePromptMessageId);
        (await Protected(state)).ShouldBe(before);
        (await state.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        (await state.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        (await state.Context.Set<VetPhotoReview>().CountAsync(r => r.Kind == "evidence", Ct)).ShouldBe(2);
        (await state.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("retained");
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Natural_lookup_filters_six_stale_previews_before_the_SQL_two_row_bound_and_refuses_current_ambiguity(bool ambiguous)
    {
        await SeedAsync(); var queries = new NaturalQueries(); await using var runtime = Build(interceptor: queries);
        var oldIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var item = (await Collect(runtime, 1)).Single(); await Drain(runtime, i + 1); var old = await CurrentReview(item.Batch); oldIds.Add(old.Id);

        }
        var current = (await Collect(runtime, 1)).Single(); await Drain(runtime, 7); var expected = await CurrentReview(current.Batch);
        if (ambiguous) { await Collect(runtime, 1); await Drain(runtime, 8); }
        await using (var stale = Open())
            await stale.Context.Set<VetPhotoReview>().Where(r => oldIds.Contains(r.Id)).ExecuteUpdateAsync(u => u.SetProperty(r => r.ProfileRevision, r => r.ProfileRevision - 1), Ct);
        await using var state = Open(); var before = await Protected(state); queries.Reads.Clear();
        using var request = runtime.Provider.CreateScope(); request.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(FamilyId);
        var found = await request.ServiceProvider.GetRequiredService<IVetPhotoWorkflowStore>().FindNaturalReviewAsync(Scope, 222, null, null, Ct);
        found.Status.ShouldBe(ambiguous ? VetPhotoWorkflowStatus.Ambiguous : VetPhotoWorkflowStatus.Existing);
        if (ambiguous) found.Review.ShouldBeNull(); else found.Review.ShouldNotBeNull().Id.ShouldBe(expected.Id);
        (await Protected(state)).ShouldBe(before); queries.Reads.Count.ShouldBe(1);
        var query = queries.Reads.Single(); query.Sql.ShouldContain("LIMIT"); query.Parameters.ShouldContain(2);
        query.Sql.ShouldContain("profile_revision"); query.Sql.ShouldContain("review_revision");
        (await state.Context.Set<VetPhotoReview>().CountAsync(r => oldIds.Contains(r.Id) && r.State == "preview", Ct)).ShouldBe(6);
    }

    private sealed class NaturalQueries : DbCommandInterceptor
    {
        public List<(string Sql, int[] Parameters)> Reads { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("vet_photo_reviews", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
                Reads.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Where(p => p.Value is int).Select(p => (int)p.Value!).ToArray()));
            return base.ReaderExecutingAsync(command, data, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData("profile")][InlineData("input")][InlineData("human_result")]
    public async Task New_profile_input_or_human_result_invalidates_the_old_notice_without_automatic_fact_or_provider_replay(string change)
    {
        await SeedAsync(); var images = new ImageProvider(); images.MissingClockReadings.Add(1);
        await using var runtime = Build(images); var item = (await Collect(runtime, 1)).Single(); await Drain(runtime, 1); var old = await CurrentReview(item.Batch);
        old.Kind.ShouldBe("evidence");
        if (change == "profile")
        {
            await using var profile = Open(); var current = await profile.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
            (await profile.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, current.Revision, [new("GlucoseUnit", null)], Ct)).Applied.ShouldBeTrue();
            await Command(runtime, "/photos_review " + item.Batch.ToString("D"));
        }
        else if (change == "input")
        {
            var bytes = Png(200); runtime.Telegram.Files["synthetic-revised-image"] = bytes;
            await Send(runtime, Text("", item.Message) with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1),
                Photo = new([new("synthetic-revised-image", "synthetic-revised-unique", 32, 24, bytes.Length)]) });
            await using var revised = Open(); var source = await revised.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);
            source.CurrentInputRevisionId.ShouldNotBe(item.Input); images.Readings[source.CurrentInputRevisionId] = 1; await Drain(runtime, 2);
        }
        else
        {
            await using var proposed = Open(); var candidate = await proposed.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
            await PhotoNatural(runtime, new { kind = "correct", batch_id = item.Batch, source_ids = new[] { item.Source },
                corrections = new[] { new { source_id = item.Source, target_candidate_revision = candidate.Revision, value = "7.3750", unit = "mmol/L", date = "2031-05-12", time = "10:01:05", offset = "+00:00", restore_requested = false } } }, actor: 222);
        }
        var fresh = await CurrentReview(item.Batch); fresh.Id.ShouldNotBe(old.Id); fresh.CompletePreviewDelivered.ShouldBeTrue();
        if (change == "human_result") fresh.Kind.ShouldBe("save"); else fresh.Kind.ShouldBe("evidence");
        await using var state = Open(); var before = await Protected(state); await Callback(runtime, old); (await Protected(state)).ShouldBe(before);
        (await state.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0); (await state.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        runtime.Images.ImageRequests.Count.ShouldBe(change == "input" ? 2 : 1);
        (await state.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(change == "input" ? 2 : 1);
        if (change == "human_result")
        {
            var human = await state.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(r => r.State == "human", Ct);
            human.InputRevisionId.ShouldBe(item.Input);
            (await state.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == human.AttemptId, Ct)).ActorUserId.ShouldBe(222);
        }
    }

    [Fact]
    public async Task Natural_cross_batch_correction_freezes_both_targets_and_accepts_only_each_fully_shown_subset()
    {
        await SeedAsync(); await using var runtime = Build();
        var one = (await Collect(runtime, 1, caption: true)).Single(); await Drain(runtime, 1); await Callback(runtime, await CurrentReview(one.Batch));
        var two = (await Collect(runtime, 1)).Single(); await Drain(runtime, 2); await Callback(runtime, await CurrentReview(two.Batch));
        await using var state = Open();
        var originals = await state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.SourceKind == "photo").OrderBy(e => e.Id).ToArrayAsync(Ct);
        originals.Length.ShouldBe(2); var insulin = await Snapshot(state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.EventType == "insulin"));
        var blobs = await Snapshot(state.Context.Set<VetPhotoBlob>().AsNoTracking());
        await Natural(runtime, new { kind = "correct" }, [Glucose("7.1250", originals[0].Id), Glucose("7.2500", originals[1].Id)]);
        var proposals = new[] { await CurrentReview(one.Batch), await CurrentReview(two.Batch) };
        proposals.All(r => r.Kind == "correction" && r.CompletePreviewDelivered).ShouldBeTrue();
        foreach (var proposal in proposals)
        {
            var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(proposal.SelectionJson, Json)!.Single();
            selected.BatchId.ShouldBe(proposal.BatchId.ShouldNotBeNull()); selected.EventId.ShouldNotBeNull();
        }
        var unchanged = await state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.SourceKind == "photo").OrderBy(e => e.Id).ToArrayAsync(Ct);
        unchanged.Select(e => (e.Id, e.Value, e.Revision, e.OccurredAt)).ShouldBe(originals.Select(e => (e.Id, e.Value, e.Revision, e.OccurredAt)));
        var human = await state.Context.Set<VetPhotoExtraction>().AsNoTracking().Where(e => e.State == "human").ToArrayAsync(Ct); human.Length.ShouldBe(2);
        (await state.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "human" && a.ActorUserId == 222, Ct)).ShouldBe(2);
        await Callback(runtime, proposals[0]);
        var first = await state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.SourceKind == "photo").OrderBy(e => e.Id).ToArrayAsync(Ct);
        first.Count(e => e.Revision == originals.Single(o => o.Id == e.Id).Revision + 1).ShouldBe(1);
        var frozenHuman = human.Single(e => e.SourceId == two.Source);
        var beforeStaleAcceptance = await Protected(state);
        await Callback(runtime, proposals[1]); (await Protected(state)).ShouldBe(beforeStaleAcceptance);
        await Command(runtime, "/photos_review " + two.Batch.ToString("D"));
        var refreshed = await VisibleReview(runtime, two.Batch); refreshed.Id.ShouldNotBe(proposals[1].Id);
        var refreshedSelection = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(refreshed.SelectionJson, Json)!.Single();
        refreshedSelection.ExtractionResultId.ShouldBe(frozenHuman.Id); refreshedSelection.EventId.ShouldBe(originals[1].Id);
        refreshedSelection.State.ShouldNotBeNull().Value.ShouldBe(7.2500m);
        await Callback(runtime, refreshed); proposals[1] = refreshed;
        (await state.Context.Set<VetPhotoExtraction>().CountAsync(e => e.State == "human", Ct)).ShouldBe(2);
        var changed = await state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.SourceKind == "photo").OrderBy(e => e.Id).ToArrayAsync(Ct);
        changed.Select(e => e.Value).ShouldBe(new[] { 7.1250m, 7.2500m });
        foreach (var e in changed)
        { var original = originals.Single(o => o.Id == e.Id); e.Revision.ShouldBe(original.Revision + 1); e.OccurredAt.ShouldBe(original.OccurredAt); e.PhotoSourceId.ShouldBe(original.PhotoSourceId); e.SourceAuthorUserId.ShouldBe(111); }
        foreach (var proposal in proposals)
        {
            var accepted = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == proposal.Id, Ct);
            accepted.DecisionActorUserId.ShouldBe(222);
            (await state.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == accepted.ActionId, Ct)).ActorUserId.ShouldBe(222);
        }
        (await Snapshot(state.Context.Set<VetEvent>().AsNoTracking().Where(e => e.EventType == "insulin"))).ShouldBe(insulin);
        (await Snapshot(state.Context.Set<VetPhotoBlob>().AsNoTracking())).ShouldBe(blobs); runtime.Images.ImageRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Import_readings_alias_starts_the_explicit_same_collection_without_any_provider_or_fact_then_photo_start_reuses_it()
    {
        await SeedAsync(); await using var runtime = Build(); await Command(runtime, "/import readings");
        await using var state = Open(); var started = await state.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        started.State.ShouldBe("collecting"); started.StarterUserId.ShouldBe(111); started.ChatId.ShouldBe(Scope.ChatId); started.TopicId.ShouldBe(Scope.TopicId);
        await Command(runtime, "/photos_start");
        var reused = await state.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct); reused.Id.ShouldBe(started.Id); reused.ReviewRevision.ShouldBe(started.ReviewRevision);
        runtime.Images.ImageRequests.ShouldBeEmpty(); (await state.Context.Set<VetEvent>().CountAsync(Ct)).ShouldBe(0);
        (await state.Context.Set<VetPhotoSource>().CountAsync(Ct)).ShouldBe(0); (await state.Context.Set<VetPhotoAttempt>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Owner_deletion_host_route_requires_two_complete_confirmations_reclaims_only_original_bytes_and_preserves_photo_and_caption_facts()
    {
        await SeedAsync(); await using var runtime = Build(); var item = await SavedOwnerDeletionPhoto(runtime);
        await using var state = Open();
        var reference = await state.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(Ct);
        var blob = await state.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync(Ct);
        var conserved = await OwnerDeletionImmutable(state); var untouched = await Protected(state);
        var initialCapacity = await OwnerDeletionCapacity(runtime); initialCapacity.ShouldBe(new VetPhotoCapacityTotals(Png(1).LongLength, 0, 1, 0, 1, 0));
        var calls = runtime.Images.ImageRequests.Count; var downloads = runtime.Telegram.DownloadedFiles.ToArray();
        await Command(runtime, "/photos_delete_originals all_originals", 111);
        var run = await state.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct);
        run.ActorUserId.ShouldBe(111); run.SelectionMode.ShouldBe("deletion_all_originals"); run.State.ShouldBe("preview"); run.SelectedCount.ShouldBe(1);
        run.FamilyId.ShouldBe(FamilyId); run.BotDbId.ShouldBe(Bot.BotDbId); run.TelegramBotId.ShouldBe(Bot.TelegramBotId); run.ChatId.ShouldBe(Scope.ChatId); run.TopicId.ShouldBe(Scope.TopicId);
        var selection = await OwnerDeletionDelivered(runtime, run.SelectionReviewId, "delete_originals_selection");
        string.Join("\n", JsonSerializer.Deserialize<string[]>(selection.PreviewPagesJson, Json)!).ShouldContain("Для повторной обработки этих оригиналов потребуется загрузить фото заново.");
        var scopeSelection = JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(selection.SelectionJson, Json)!.Single();
        scopeSelection.SourceId.ShouldBe(item.Source); scopeSelection.InputRevisionId.ShouldBe(item.Input); scopeSelection.ReferenceId.ShouldBe(reference.Id);
        scopeSelection.Revision.ShouldBe(reference.Revision); scopeSelection.BlobId.ShouldBe(blob.Id); scopeSelection.ExpectedCurrentInputId.ShouldBe(item.Input);
        selection.RequesterUserId.ShouldBe(111); selection.SelectionJson.ShouldBe(run.SelectionJson);
        (await Protected(state)).ShouldBe(untouched); (await OwnerDeletionCapacity(runtime)).ShouldBe(initialCapacity);
        await Callback(runtime, selection, actor: 111);
        var acceptedScope = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == selection.Id, Ct);
        acceptedScope.State.ShouldBe("accepted"); acceptedScope.DecisionActorUserId.ShouldBe(111); acceptedScope.ActionId.ShouldBeNull();
        (await state.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("approved");
        var awaiting = await state.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct); awaiting.State.ShouldBe("awaiting_continue"); awaiting.ComparisonReviewId.ShouldBeNull();
        (await Protected(state)).ShouldBe(untouched); (await OwnerDeletionCapacity(runtime)).ShouldBe(initialCapacity);
        await Command(runtime, "/photos_continue " + run.Id.ToString("D"), 111);
        var window = await state.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct);
        window.RunId.ShouldBe(run.Id); window.Ordinal.ShouldBe(0); window.State.ShouldBe("awaiting_review");
        var deletion = await OwnerDeletionDelivered(runtime, window.ComparisonReviewId.ShouldNotBeNull(), "delete_originals");
        deletion.RunWindowId.ShouldBe(window.Id); deletion.RequesterUserId.ShouldBe(111);
        var exact = JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(deletion.SelectionJson, Json)!.Single();
        exact.ShouldBe(scopeSelection); exact.ExpectedBlobRetainedReferences.ShouldBe(1); exact.EventId.ShouldNotBeNull();
        var pages = string.Join("\n", JsonSerializer.Deserialize<string[]>(deletion.PreviewPagesJson, Json)!);
        pages.ShouldContain(item.Source.ToString("D")); pages.ShouldContain(item.Input.ToString("D")); pages.ShouldContain(reference.Id.ToString("D"));
        pages.ShouldContain("ревизия " + reference.Revision); pages.ShouldContain(Png(1).LongLength.ToString(CultureInfo.InvariantCulture) + " байт");
        pages.ShouldContain("связанный факт №" + exact.EventId); pages.ShouldContain("останется 0 ссылок"); pages.ShouldContain("Для повторной обработки этих оригиналов потребуется загрузить фото заново.");
        (await Protected(state)).ShouldBe(untouched); (await OwnerDeletionCapacity(runtime)).ShouldBe(initialCapacity);
        await Callback(runtime, deletion, actor: 111);
        runtime.Telegram.SentMessages.ShouldContain(m => m.ChatId == Scope.ChatId && m.TopicId == Scope.TopicId && m.Text == "Удаление оригиналов: Applied. Факты и происхождение сохранены.");
        var tombstone = await state.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(Ct);
        tombstone.Id.ShouldBe(reference.Id); tombstone.InputRevisionId.ShouldBe(item.Input); tombstone.BlobId.ShouldBe(blob.Id);
        tombstone.ContentHash.ShouldBe(reference.ContentHash); tombstone.State.ShouldBe("deleted"); tombstone.Revision.ShouldBe(reference.Revision + 1);
        tombstone.DeletedByUserId.ShouldBe(111); tombstone.DeletedAt.ShouldBe(Now); tombstone.DeletionReviewId.ShouldBe(deletion.Id);
        var pending = await state.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync(Ct);
        pending.State.ShouldBe("reclaim_pending"); pending.Content.ShouldNotBeNull().ShouldBe(Png(1));
        (await OwnerDeletionCapacity(runtime)).ShouldBe(new VetPhotoCapacityTotals(Png(1).LongLength, 0, 0, 0, 1, 0));
        var committed = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == deletion.Id, Ct);
        committed.State.ShouldBe("accepted"); committed.DecisionActorUserId.ShouldBe(111); committed.ActionId.ShouldBeNull();
        var outcome = JsonSerializer.Deserialize<VetPhotoOriginalDeletionResult>(committed.OutcomeJson!, Json)!;
        outcome.Status.ShouldBe(VetMutationStatus.Applied); outcome.ReferenceCount.ShouldBe(1); outcome.ReclaimableBytes.ShouldBe(Png(1).LongLength);
        (await state.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync(Ct)).State.ShouldBe("completed");
        var completed = await state.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(Ct); completed.State.ShouldBe("completed"); completed.NextWindowOrdinal.ShouldBe(1);
        (await OwnerDeletionImmutable(state)).ShouldBe(conserved);
        (await state.Context.Set<VetPhotoReaderLease>().CountAsync(l => l.ReleasedAt == null && l.ExpiresAt > Now, Ct)).ShouldBe(0);
        await Pass(runtime);
        var reclaimed = await state.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync(Ct);
        reclaimed.Id.ShouldBe(blob.Id); reclaimed.ContentHash.ShouldBe(blob.ContentHash); reclaimed.ActualBytes.ShouldBe(blob.ActualBytes);
        reclaimed.State.ShouldBe("reclaimed"); reclaimed.Content.ShouldBeNull(); reclaimed.ReclaimedAt.ShouldBe(Now);
        (await OwnerDeletionCapacity(runtime)).ShouldBe(new VetPhotoCapacityTotals(0, 0, 0, 0, 1, 0));
        using (var request = runtime.Provider.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(FamilyId);
            var unavailable = await request.ServiceProvider.GetRequiredService<IVetPhotoArchiveStore>().ReserveDownloadAsync(Scope, item.Source, item.Input, 111, Ct);
            unavailable.Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted); unavailable.Claim.ShouldBeNull();
        }
        var finished = await OwnerDeletionAll(state);
        runtime.Telegram.ClearSent(); await Command(runtime, "/photos_reprocess all_originals", 111);
        runtime.Telegram.SentMessages.ShouldContain(m => m.ChatId == Scope.ChatId && m.TopicId == Scope.TopicId
            && m.Text == "Выбранные оригиналы недоступны; для повторной обработки удалённых байтов загрузите фото заново. Новые вызовы не начаты.");
        (await OwnerDeletionAll(state)).ShouldBe(finished);
        await Callback(runtime, deletion, actor: 111); await Pass(runtime);
        (await OwnerDeletionAll(state)).ShouldBe(finished); (await OwnerDeletionImmutable(state)).ShouldBe(conserved);
        (await OwnerDeletionCapacity(runtime)).ShouldBe(new VetPhotoCapacityTotals(0, 0, 0, 0, 1, 0));
        runtime.Images.ImageRequests.Count.ShouldBe(calls); calls.ShouldBe(1); runtime.Telegram.DownloadedFiles.ToArray().ShouldBe(downloads); downloads.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Member_deletion_host_command_is_owner_only_and_cannot_create_a_run_review_or_change_originals_facts_or_accounting()
    {
        await SeedAsync(); await using var runtime = Build(); await SavedOwnerDeletionPhoto(runtime);
        await using var state = Open(); var before = await OwnerDeletionAll(state); var capacity = await OwnerDeletionCapacity(runtime);
        var downloads = runtime.Telegram.DownloadedFiles.ToArray(); runtime.Telegram.ClearSent();
        await Command(runtime, "/photos_delete_originals all_originals", 222);
        (await state.Context.Set<VetPhotoRun>().CountAsync(Ct)).ShouldBe(0);
        (await state.Context.Set<VetPhotoRunWindow>().CountAsync(Ct)).ShouldBe(0);
        (await state.Context.Set<VetPhotoReview>().CountAsync(r => r.Kind == "delete_originals_selection" || r.Kind == "delete_originals", Ct)).ShouldBe(0);
        (await OwnerDeletionAll(state)).ShouldBe(before); (await OwnerDeletionCapacity(runtime)).ShouldBe(capacity);
        runtime.Telegram.SentMessages.ShouldContain(m => m.ChatId == Scope.ChatId && m.TopicId == Scope.TopicId
            && m.Text == "Удалять оригиналы может только владелец; выбранная область недоступна или устарела. Новые действия не начаты.");
        runtime.Images.ImageRequests.Count.ShouldBe(1); runtime.Telegram.DownloadedFiles.ToArray().ShouldBe(downloads); downloads.Length.ShouldBe(1);
    }

    private async Task<Item> SavedOwnerDeletionPhoto(Runtime runtime)
    {
        var item = (await Collect(runtime, 1, caption: true)).Single(); await Drain(runtime, 1);
        var save = await CurrentReview(item.Batch); await Callback(runtime, save, actor: 222);
        await using var state = Open(); var facts = await state.Context.Set<VetEvent>().AsNoTracking().ToArrayAsync(Ct); facts.Length.ShouldBe(2);
        var glucose = facts.Single(e => e.SourceKind == "photo"); glucose.PhotoSourceId.ShouldBe(item.Source); glucose.InputRevisionId.ShouldBe(item.Input);
        glucose.SourceAuthorUserId.ShouldBe(111); glucose.Value.ShouldBe(5.0100m); glucose.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-12T10:01:05Z", CultureInfo.InvariantCulture));
        var insulin = facts.Single(e => e.EventType == "insulin"); insulin.SourceKind.ShouldBe("text"); insulin.Value.ShouldBe(2.1250m);
        insulin.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-12T09:00:00Z", CultureInfo.InvariantCulture));
        var accepted = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == save.Id, Ct); accepted.State.ShouldBe("accepted"); accepted.DecisionActorUserId.ShouldBe(222);
        (await state.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == accepted.ActionId, Ct)).ActorUserId.ShouldBe(222);
        (await state.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).SourceAuthorUserId.ShouldBe(111);
        (await state.Context.Set<VetExtractionResult>().AsNoTracking().SingleAsync(Ct)).Json.ShouldContain("2.1250");
        runtime.Images.ImageRequests.Count.ShouldBe(1); return item;
    }

    private async Task<VetPhotoReview> OwnerDeletionDelivered(Runtime runtime, Guid reviewId, string kind)
    {
        await using var state = Open(); var review = await state.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == reviewId, Ct);
        review.Kind.ShouldBe(kind); review.State.ShouldBe("preview"); review.CompletePreviewDelivered.ShouldBeTrue(); review.AcceptancePromptMessageId.ShouldNotBeNull();
        review.FamilyId.ShouldBe(FamilyId); review.BotDbId.ShouldBe(Bot.BotDbId); review.TelegramBotId.ShouldBe(Bot.TelegramBotId); review.ChatId.ShouldBe(Scope.ChatId); review.TopicId.ShouldBe(Scope.TopicId);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!; var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        pages.Length.ShouldBe(review.PageCount); pages.Length.ShouldBeGreaterThan(0); pages.Length.ShouldBeLessThanOrEqualTo(64); deliveries.Length.ShouldBe(pages.Length);
        deliveries.Select(p => p.PageIndex).Order().ShouldBe(Enumerable.Range(0, pages.Length)); deliveries.Select(p => p.MessageId).Distinct().Count().ShouldBe(pages.Length);
        for (var index = 0; index < pages.Length; index++)
        {
            pages[index].Length.ShouldBeLessThanOrEqualTo(3500); var delivered = deliveries.Single(p => p.PageIndex == index); delivered.MessageId.ShouldBeGreaterThan(0);
            delivered.TextHash.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pages[index]))).ToLowerInvariant());
            var page = pages[index]; runtime.Telegram.SentMessages.ShouldContain(m => m.ChatId == Scope.ChatId && m.TopicId == Scope.TopicId && m.Text == page);
        }
        review.Fingerprint.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(review.SelectionJson))).ToLowerInvariant());
        var prompt = deliveries.Single(p => p.PageIndex == pages.Length - 1).MessageId; review.AcceptancePromptMessageId.ShouldBe(prompt);
        var visible = runtime.Telegram.EditedButtons.Single(e => e.Buttons.Any(b => b.CallbackData == VetPhotoReviewComposer.Callback("a", review)));
        visible.Chat.ShouldBe(Scope.ChatId); visible.Message.ShouldBe(prompt); visible.Buttons.Select(b => b.CallbackData).ShouldContain(VetPhotoReviewComposer.Callback("d", review));
        return review;
    }

    private async Task<VetPhotoCapacityTotals> OwnerDeletionCapacity(Runtime runtime)
    {
        using var request = runtime.Provider.CreateScope(); request.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(FamilyId);
        return await request.ServiceProvider.GetRequiredService<IVetPhotoArchiveStore>().GetCapacityAsync(Scope, 111, Ct);
    }
    private static async Task<string> OwnerDeletionImmutable(VetTestSession s)
    {
        var photoMessageIds = await s.Context.Set<VetPhotoSource>().AsNoTracking().Select(x => x.SourceMessageDbId).ToArrayAsync(Ct);
        var captionSourceIds = await s.Context.Set<VetTextSource>().AsNoTracking().Where(x => photoMessageIds.Contains(x.SourceMessageDbId)).Select(x => x.Id).ToArrayAsync(Ct);
        captionSourceIds.Length.ShouldBe(1);
        var captionInputIds = await s.Context.Set<VetTextSourceRevision>().AsNoTracking().Where(x => captionSourceIds.Contains(x.SourceId)).Select(x => x.Id).ToArrayAsync(Ct);
        return JsonSerializer.Serialize(new
        {
        Facts = await Snapshot(s.Context.Set<VetEvent>().AsNoTracking().OrderBy(x => x.Id)),
        Actions = await Snapshot(s.Context.VetDiaryActions.AsNoTracking().OrderBy(x => x.Id)),
        Candidates = await Snapshot(s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id)),
        Sources = await Snapshot(s.Context.Set<VetPhotoSource>().AsNoTracking().OrderBy(x => x.Id)),
        Inputs = await Snapshot(s.Context.Set<VetPhotoInputRevision>().AsNoTracking().OrderBy(x => x.Id)),
        Results = await Snapshot(s.Context.Set<VetPhotoExtraction>().AsNoTracking().OrderBy(x => x.Id)),
        Attempts = await Snapshot(s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(x => x.Id)),
        TextSources = await Snapshot(s.Context.Set<VetTextSource>().AsNoTracking().Where(x => captionSourceIds.Contains(x.Id)).OrderBy(x => x.Id)),
        TextInputs = await Snapshot(s.Context.Set<VetTextSourceRevision>().AsNoTracking().Where(x => captionSourceIds.Contains(x.SourceId)).OrderBy(x => x.Id)),
        TextResults = await Snapshot(s.Context.Set<VetExtractionResult>().AsNoTracking().Where(x => captionInputIds.Contains(x.InputRevisionId)).OrderBy(x => x.Id)),
        Accounting = await Snapshot(s.Context.LlmCalls.AsNoTracking().OrderBy(x => x.Id))
        }, Json);
    }
    private static async Task<string> OwnerDeletionAll(VetTestSession s) => JsonSerializer.Serialize(new
    {
        Immutable = await OwnerDeletionImmutable(s),
        Reviews = await Snapshot(s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(x => x.Id)),
        Runs = await Snapshot(s.Context.Set<VetPhotoRun>().AsNoTracking().OrderBy(x => x.Id)),
        Windows = await Snapshot(s.Context.Set<VetPhotoRunWindow>().AsNoTracking().OrderBy(x => x.Id)),
        References = await Snapshot(s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x => x.Id)),
        Blobs = await Snapshot(s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x => x.Id)),
        Batches = await Snapshot(s.Context.Set<VetPhotoBatch>().AsNoTracking().OrderBy(x => x.Id))
    }, Json);


    private sealed class Runtime(ServiceProvider provider, ImageProvider images, ReviewTelegram telegram, long family) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public ImageProvider Images { get; } = images;
        public ReviewTelegram Telegram { get; } = telegram;
        public long Family { get; } = family;
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
    private sealed class ImageProvider : IChatClient, IImageChatClient
    {
        public bool SupportsImages => true;
        public ConcurrentDictionary<Guid, int> Readings { get; } = new();
        public ConcurrentQueue<string> Text { get; } = new();
        public HashSet<Guid> UnknownInputs { get; } = [];
        public HashSet<int> MissingClockReadings { get; } = [];
        public int? UnknownCallNumber { get; set; }
        public List<(Guid Source, Guid Input, string? Model, int Bytes)> ImageRequests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> input, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var messages = input.ToArray(); var images = messages.SelectMany(m => m.Contents).OfType<DataContent>().ToArray(); string response;
            if (images.Length > 0)
            {
                images.Length.ShouldBe(1); options!.ModelId.ShouldBe("gpt-6.1-sol"); var user = messages.Last(m => m.Role == ChatRole.User).Text;
                using var json = JsonDocument.Parse(user); var source = json.RootElement.GetProperty("photo_source_id").GetGuid(); var revision = json.RootElement.GetProperty("input_revision_id").GetGuid();
                source.ShouldNotBe(Guid.Empty); revision.ShouldNotBe(Guid.Empty); Readings.TryGetValue(revision, out var reading).ShouldBeTrue();
                ImageRequests.Add((source, revision, options.ModelId, images[0].Data.Length));
                if (UnknownInputs.Contains(revision) || UnknownCallNumber == ImageRequests.Count) throw new IOException("Synthetic unknown provider outcome.");
                response = JsonSerializer.Serialize(new { schema_version = 1, photo_source_id = source, input_revision_id = revision, kind = "meter",
                    displays = new[] { new { value_text = Value(reading).ToString("0.0000", CultureInfo.InvariantCulture), decimal_value = Value(reading), unit = "mmol/L", year = 2031,
                        year_displayed = true, month = 5, day = 12, time = MissingClockReadings.Contains(reading) ? null : At(reading).ToString("HH:mm:ss", CultureInfo.InvariantCulture), offset = "+00:00" } }, reasons = Array.Empty<string>(), notes = (string?)null }, Json);
            }
            else if (!Text.TryDequeue(out response!)) response = "{\"needs_reply\":false,\"events\":[],\"unclear\":[]}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)) { Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 11 } });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class ReviewTelegram : FakeTelegramClient, ITelegramClient
    {
        public bool FailAfterFirstReviewPage { get; set; }
        public bool FailEveryReviewPage { get; set; }
        public List<(long Chat, int Message, IReadOnlyList<InlineButton> Buttons)> EditedButtons { get; } = [];
        public new Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken ct)
        { EditedButtons.Add((chatId, messageId, buttons.ToArray())); return base.EditMessageButtonsAsync(chatId, messageId, buttons, ct); }
        private int pages;
        public new Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken)
        {
            var reviewPage = text.Length > 500 && text.Contains("Источник", StringComparison.Ordinal);
            if (reviewPage && text.StartsWith("Партия ", StringComparison.Ordinal)) pages = 0;
            if (reviewPage && (FailEveryReviewPage || FailAfterFirstReviewPage && ++pages > 1)) throw new IOException("Synthetic full review page send failure.");
            return base.SendTextAsync(chatId, topicId, text, replyToMessageId, cancellationToken);
        }
    }
    private sealed class Clients(ITelegramClient client) : ITelegramClientFactory { public ITelegramClient Create(string token) => client; }
    private sealed class NoopBudget : IBudgetNoticeDispatcher { public Task Dispatch() => Task.CompletedTask; }
    private sealed class NoopManager : IManagerUpdateHandler { public Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingUpdate update, CancellationToken ct) => Task.CompletedTask; }
    private sealed class NoopGeneral : IGeneralAssistant { public Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, StoreResult store, CancellationToken ct, bool replyToAll = false) => Task.CompletedTask; }
    private sealed class NoopHealth : IHealthAssistant
    {
        public Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, StoreResult store, CancellationToken ct, bool replyToAll = false) => Task.CompletedTask;
        public Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo query, CancellationToken ct) => Task.CompletedTask;
    }
}
