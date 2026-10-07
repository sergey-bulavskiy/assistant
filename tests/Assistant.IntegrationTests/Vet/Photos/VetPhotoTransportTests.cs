using System.Data.Common;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoTransportTests : VetPhotoPresentationFixture
{
    private sealed class ClientFactory(FakeTelegramClient telegram) : ITelegramClientFactory
    { public ITelegramClient Create(string token) => telegram; }
    private sealed class Operations : IVetPhotoApplicationOperations
    {
        public int Resumes;
        public Task ResumeRunsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct) { Resumes++; return Task.CompletedTask; }
        public Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work, VetPhotoProcessResult result, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, string command, string? args, Guid key, CancellationToken ct) => throw new InvalidOperationException("Unexpected operation dispatch.");
        public Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, VetPhotoOperation operation, Guid key, CancellationToken ct) => throw new InvalidOperationException("Unexpected operation dispatch.");
        public Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct) => throw new InvalidOperationException("Unexpected callback dispatch.");
    }
    private VetPhotoAssistant Assistant(VetTestSession s, Operations? operations = null)
    {
        var approvals = new ApprovalService(s.Context, new ClientFactory(s.Telegram), Options.Create(new BotOptions
        { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" }), Clock);
        return new(Photos(s), Photos(s), Photos(s), Photos(s), Composer(s), operations ?? new(), approvals,
            NullLogger<VetPhotoAssistant>.Instance);
    }
    private async Task<(VetPhotoAdmission Admission, IncomingMessage Message, StoreResult Stored)> Unbound(VetTestSession s, int number = 1, MessageKind kind = MessageKind.Photo)
    {
        var message = Text("synthetic transport caption", number) with { Kind = kind };
        var update = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var admitted = await Photos(s).AdmitAsync(Scope, message, update,
            new("synthetic-transport-file", null, "synthetic.png", "image/png", 100, 32, 24), null, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        admitted.Source!.SourceMessageDbId.ShouldBeNull();
        (await s.Context.Bots.AsNoTracking().Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct)).ShouldBe(update - 1);
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, update, message, Ct);
        stored.Outcome.ShouldBe(StoreOutcome.Stored); stored.MessageDbId.ShouldNotBeNull();
        return (admitted, message, stored);
    }

    [Theory]
    [InlineData(MessageKind.Photo)]
    [InlineData(MessageKind.Document)]
    public async Task Metadata_precedes_offset_then_restart_binds_exact_Telegram_bot_transport_without_rewriting_messages(MessageKind kind)
    {
        await SeedAsync(); Guid source; long messageId; long offset; string messages;
        await using (var s = Open())
        {
            var data = await Unbound(s, kind: kind); source = data.Admission.Source!.Id; messageId = data.Stored.MessageDbId!.Value;
            (await s.Context.Messages.SingleAsync(Ct)).BotId.ShouldBe(Bot.TelegramBotId); Bot.TelegramBotId.ShouldNotBe(Bot.BotDbId);
            var lost = await s.Messages.StoreAsync(Bot.TelegramBotId, 1, data.Message, Ct);
            lost.Outcome.ShouldBe(StoreOutcome.AlreadyProcessed); lost.MessageDbId.ShouldBeNull();
            offset = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct);
            messages = JsonSerializer.Serialize(await s.Context.Messages.AsNoTracking().ToArrayAsync(Ct), Json);
        }
        await using var restarted = Open();
        (await Photos(restarted).BindStoredMessageAsync(Scope, source, 111, Ct)).ShouldBeTrue();
        (await Photos(restarted).BindStoredMessageAsync(Scope, source, 222, Ct)).ShouldBeTrue();
        (await Photos(restarted).GetSourceAsync(Scope, source, Ct))!.Source!.SourceMessageDbId.ShouldBe(messageId);
        JsonSerializer.Serialize(await restarted.Context.Messages.AsNoTracking().ToArrayAsync(Ct), Json).ShouldBe(messages);
        (await restarted.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct)).ShouldBe(offset);
        await NoFacts(restarted);
    }

    [Theory]
    [InlineData("family")]
    [InlineData("bot")]
    [InlineData("chat")]
    [InlineData("topic")]
    [InlineData("null_topic")]
    [InlineData("message")]
    [InlineData("author")]
    [InlineData("direction")]
    [InlineData("kind")]
    [InlineData("chat_type")]
    [InlineData("sent_at")]
    public async Task Every_immutable_transport_mismatch_refuses_direct_and_restart_binding_and_stays_visibly_pending(string mismatch)
    {
        await SeedAsync(); await using var s = Open(); var data = await Unbound(s);
        var row = await Db.Messages.SingleAsync(m => m.Id == data.Stored.MessageDbId, Ct);
        switch (mismatch)
        {
            case "family": var family = new Family { Name = "synthetic foreign family", CreatedAt = Now }; Db.Add(family); await Db.SaveChangesAsync(Ct); row.FamilyId = family.Id; break;
            case "bot": row.BotId = Bot.BotDbId; break;
            case "chat": row.ChatId = -200; break;
            case "topic": row.TopicId = 8; break;
            case "null_topic": row.TopicId = null; break;
            case "message": row.TelegramMessageId = 999; break;
            case "author": row.UserId = 222; break;
            case "direction": row.Direction = MessageDirection.Out; break;
            case "kind": row.Kind = MessageKind.Text; break;
            case "chat_type": row.ChatType = "group"; break;
            case "sent_at": row.SentAt = Now.AddSeconds(1); break;
        }
        await Db.SaveChangesAsync(Ct); var before = await Snapshot(s);
        (await Photos(s).BindMessageAsync(Scope, data.Admission.Source!.Id, data.Stored.MessageDbId!.Value, Ct)).ShouldBeFalse();
        await using var restarted = Open();
        (await Photos(restarted).BindStoredMessageAsync(Scope, data.Admission.Source.Id, 111, Ct)).ShouldBeFalse();
        await Assistant(restarted).BindAsync(Bot, data.Message, data.Stored, data.Admission, Ct);
        await Assistant(restarted).HandleAdmissionAsync(Bot, restarted.Telegram, data.Message, data.Stored, data.Admission, Ct);
        restarted.Telegram.SentMessages.Single().Text.ShouldContain("связь с сообщением не подтверждена");
        restarted.Telegram.SentMessages.Single().Text.ShouldContain("Обработка не начата");
        (await Snapshot(restarted)).ShouldBe(before);
        (await restarted.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0); await NoFacts(restarted);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("place")]
    [InlineData("bot")]
    [InlineData("foreign_topic")]
    public async Task Restart_binding_rechecks_current_approval_and_exact_scope_before_any_pointer_change(string gate)
    {
        await SeedAsync(); Guid source; await using (var s = Open()) source = (await Unbound(s)).Admission.Source!.Id;
        if (gate == "actor") await Db.Set<FamilyMember>().Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (gate == "place") await Db.Set<Place>().Where(p => p.BotId == Bot.BotDbId && p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled), Ct);
        if (gate == "bot") await Db.Bots.Where(b => b.Id == Bot.BotDbId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        await using var restarted = Open(); var before = await Snapshot(restarted);
        (await Photos(restarted).BindStoredMessageAsync(gate == "foreign_topic" ? Scope with { TopicId = 8 } : Scope, source, 111, Ct)).ShouldBeFalse();
        (await Snapshot(restarted)).ShouldBe(before); await NoFacts(restarted);
    }

    [Fact]
    public async Task Foreign_family_source_identifier_is_not_visible_or_bindable_in_current_family()
    {
        await SeedAsync(); Guid source; await using (var s = Open()) source = (await Unbound(s)).Admission.Source!.Id;
        var family = new Family { Name = "synthetic second family", CreatedAt = Now }; Db.Add(family); await Db.SaveChangesAsync(Ct);
        var bot = new Assistant.Domain.Bots.Bot { FamilyId = family.Id, TelegramBotId = 2001, Username = "synthetic_second_vet", Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(bot); Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "synthetic second owner", IsOwner = true, Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        await Db.SaveChangesAsync(Ct); Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 7, Title = "synthetic second place", Status = PlaceStatus.Approved, CreatedAt = Now }); await Db.SaveChangesAsync(Ct);
        await using var foreign = Open(family.Id); var scope = new VetDiaryScope(family.Id, bot.Id, bot.TelegramBotId, -200, 7);
        (await Photos(foreign).BindStoredMessageAsync(scope, source, 111, Ct)).ShouldBeFalse();
        (await Photos(foreign).GetSourceAsync(scope, source, Ct)).ShouldBeNull();
        await using var verify = Open(); (await Photos(verify).GetSourceAsync(Scope, source, Ct))!.Source!.SourceMessageDbId.ShouldBeNull(); await NoFacts(verify);
    }

    [Fact]
    public async Task Transient_binding_database_exception_propagates_and_restart_recovers_without_a_second_message()
    {
        await SeedAsync(); Guid source; long messageId;
        await using (var s = Open()) { var data = await Unbound(s); source = data.Admission.Source!.Id; messageId = data.Stored.MessageDbId!.Value; }
        var failure = new FailMessageRead(); await using (var failing = Open(interceptor: failure))
            await Should.ThrowAsync<InvalidOperationException>(() => Photos(failing).BindStoredMessageAsync(Scope, source, 111, Ct));
        failure.Hits.ShouldBe(1); await using var restarted = Open();
        (await Photos(restarted).BindStoredMessageAsync(Scope, source, 111, Ct)).ShouldBeTrue();
        (await Photos(restarted).GetSourceAsync(Scope, source, Ct))!.Source!.SourceMessageDbId.ShouldBe(messageId);
        (await restarted.Context.Messages.CountAsync(Ct)).ShouldBe(1); await NoFacts(restarted);
    }
    private sealed class FailMessageRead : DbCommandInterceptor
    {
        public int Hits;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM messages", StringComparison.Ordinal)) { Hits++; throw new InvalidOperationException("synthetic binding database failure"); }
            return base.ReaderExecutingAsync(command, data, result, ct);
        }
    }

    [Fact]
    public async Task Resume_binds_only_five_sources_using_each_original_author_then_next_pass_recovers_remainder_without_dispatch()
    {
        await SeedAsync(); var operations = new Operations(); await using var s = Open();
        var sources = new List<Guid>(); for (var n = 1; n <= 6; n++) sources.Add((await Unbound(s, n)).Admission.Source!.Id);
        var assistant = Assistant(s, operations); await assistant.ResumeAsync(Bot, s.Telegram, Ct);
        (await s.Context.Set<VetPhotoSource>().CountAsync(x => x.SourceMessageDbId != null, Ct)).ShouldBe(5);
        (await s.Context.Set<VetPhotoSource>().CountAsync(x => x.SourceMessageDbId == null, Ct)).ShouldBe(1);
        await assistant.ResumeAsync(Bot, s.Telegram, Ct);
        (await s.Context.Set<VetPhotoSource>().CountAsync(x => x.SourceMessageDbId != null, Ct)).ShouldBe(6);
        (await s.Context.Messages.CountAsync(Ct)).ShouldBe(6); operations.Resumes.ShouldBe(2);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0); s.Telegram.DownloadedFiles.ShouldBeEmpty(); await NoFacts(s);
    }

    [Fact]
    public async Task Resume_recovers_actual_complete_composer_pages_once_and_waits_for_a_new_explicit_decision()
    {
        await SeedAsync(); var operations = new Operations(); Guid batch;
        await using (var s = Open()) batch = (await Prepare(s)).Source.BatchId!.Value;
        await using var restarted = Open(); var assistant = Assistant(restarted, operations);
        await assistant.ResumeAsync(Bot, restarted.Telegram, Ct);
        var review = await restarted.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(Ct);
        review.CompletePreviewDelivered.ShouldBeTrue(); review.State.ShouldBe("preview"); review.RequesterUserId.ShouldBe(111);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        var delivered = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        delivered.Length.ShouldBe(pages.Length);
        for (var index = 0; index < pages.Length; index++) { delivered[index].PageIndex.ShouldBe(index); delivered[index].TextHash.ShouldBe(Hash(pages[index])); restarted.Telegram.SentMessages.ShouldContain(m => m.Text == pages[index]); }
        review.AcceptancePromptMessageId.ShouldBe(delivered[^1].MessageId); await NoFacts(restarted);
        restarted.Telegram.SentMessages.ShouldContain(m => m.Text.Contains(batch.ToString("D")));
        var sent = restarted.Telegram.SentMessages.Count; var edits = restarted.Telegram.TextEdits.Count;
        await assistant.ResumeAsync(Bot, restarted.Telegram, Ct);
        restarted.Telegram.SentMessages.Count.ShouldBe(sent); restarted.Telegram.TextEdits.Count.ShouldBe(edits);
        (await restarted.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(1); operations.Resumes.ShouldBe(2);
        restarted.Telegram.DownloadedFiles.ShouldBeEmpty(); await NoFacts(restarted);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("place")]
    [InlineData("bot")]
    public async Task Resume_does_not_bind_or_preview_when_original_author_place_or_bot_has_been_revoked(string gate)
    {
        await SeedAsync(); Guid source; await using (var s = Open()) source = (await Unbound(s)).Admission.Source!.Id;
        if (gate == "actor") await Db.Set<FamilyMember>().Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (gate == "place") await Db.Set<Place>().Where(p => p.BotId == Bot.BotDbId && p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled), Ct);
        if (gate == "bot") await Db.Bots.Where(b => b.Id == Bot.BotDbId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        await using var restarted = Open(); var before = await Snapshot(restarted);
        await Assistant(restarted).ResumeAsync(Bot, restarted.Telegram, Ct);
        (await Snapshot(restarted)).ShouldBe(before);
        (await Photos(restarted).GetSourceAsync(Scope, source, Ct))!.Source!.SourceMessageDbId.ShouldBeNull();
        (await restarted.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(0);
        restarted.Telegram.SentMessages.ShouldBeEmpty(); restarted.Telegram.DownloadedFiles.ShouldBeEmpty(); await NoFacts(restarted);
    }
}
