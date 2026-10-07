using System.Data.Common;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Messages;
using Assistant.Application.Manager;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Health.Documents;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Assistant.IntegrationTests.Health;

public sealed class HealthDocumentStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken NoCancellation = CancellationToken.None;
    private readonly TestClock _clock = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unset_or_other_family_scope_refuses_read_and_admission(bool otherFamily)
    {
        var seed = await SeedAsync();
        await using var scope = Open(otherFamily ? seed.Scope.FamilyId + 100 : null);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Documents.GetLatestAsync(seed.Scope, NoCancellation));
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation));
        await using var verify = Open(seed.Scope.FamilyId);
        (await verify.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(0);
        (await verify.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("internal_bot")]
    [InlineData("telegram_bot")]
    public async Task Wrong_profile_or_bot_scope_cannot_create_or_list_documents(string wrong)
    {
        var seed = await SeedAsync();
        var sibling = await SeedAsync(telegramBotId: 1002, existingFamilyId: seed.Scope.FamilyId);
        var bad = wrong switch
        {
            "profile" => seed.Scope with { ProfileId = sibling.Scope.ProfileId },
            "internal_bot" => seed.Scope with { BotDbId = sibling.Scope.BotDbId },
            _ => seed.Scope with { TelegramBotId = sibling.Scope.TelegramBotId }
        };
        await using var scope = Open(seed.Scope.FamilyId);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Documents.AdmitAsync(bad, seed.Message, 10, NoCancellation));
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Documents.GetContextAsync(bad, NoCancellation));
        (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(0);
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Binding_refuses_another_message_and_exact_nullable_topic_mismatch()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        var wrongMessage = seed.Message with { MessageId = 600, TopicId = null };
        var wrong = await scope.Messages.StoreAsync(seed.Scope.TelegramBotId, 11, wrongMessage, NoCancellation);
        var unbound = (await scope.Documents.BindAsync(seed.Scope, admission.Id, wrong.MessageDbId, NoCancellation)).ShouldNotBeNull();
        unbound.SourceMessageId.ShouldBeNull();
        (await scope.Documents.FindAsync(seed.Scope, seed.Message.ChatId, null, seed.Message.MessageId, NoCancellation)).ShouldBeNull();
        (await scope.Documents.TryClaimAsync(unbound, NoCancellation)).ShouldBeNull();
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
        var stored = await scope.Messages.StoreAsync(seed.Scope.TelegramBotId, 12, seed.Message, NoCancellation);
        var bound = (await scope.Documents.BindAsync(seed.Scope, admission.Id, stored.MessageDbId, NoCancellation)).ShouldNotBeNull();
        bound.SourceMessageId.ShouldBe(stored.MessageDbId);
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("author")]
    [InlineData("sent_at")]
    [InlineData("kind")]
    [InlineData("direction")]
    public async Task Binding_requires_exact_original_incoming_source_identity(string wrong)
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        var message = wrong switch
        {
            "topic" => seed.Message with { TopicId = null },
            "author" => seed.Message with { UserId = 222 },
            "sent_at" => seed.Message with { SentAt = Now.AddSeconds(1) },
            "kind" => seed.Message with { Kind = MessageKind.Text },
            _ => seed.Message
        };
        var stored = await scope.Messages.StoreAsync(seed.Scope.TelegramBotId, 10, message, NoCancellation);
        if (wrong == "direction") await scope.Context.Messages.Where(m => m.Id == stored.MessageDbId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Direction, MessageDirection.Out));
        var bound = (await scope.Documents.BindAsync(seed.Scope, admission.Id, stored.MessageDbId, NoCancellation)).ShouldNotBeNull();
        bound.SourceMessageId.ShouldBeNull();
        (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldBeNull();
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
        (await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync()).AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Binding_refuses_another_familys_stored_message()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync(telegramBotId: 1002);
        long? otherSource;
        await using (var foreign = Open(other.Scope.FamilyId))
            otherSource = (await foreign.Messages.StoreAsync(other.Scope.TelegramBotId, 10, other.Message, NoCancellation)).MessageDbId;
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        var bound = (await scope.Documents.BindAsync(seed.Scope, admission.Id, otherSource, NoCancellation)).ShouldNotBeNull();
        bound.SourceMessageId.ShouldBeNull();
        (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldBeNull();
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Redelivery_preserves_one_immutable_source_and_repost_creates_another()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var first = await RetainAsync(scope, seed, seed.Message, 10, "first retained text");
        var again = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 11, NoCancellation)).ShouldNotBeNull();
        again.Id.ShouldBe(first.Admission.Id);
        var duplicate = await scope.Messages.StoreAsync(seed.Scope.TelegramBotId, 11, seed.Message, NoCancellation);
        duplicate.Outcome.ShouldBe(StoreOutcome.Duplicate);
        var rebound = (await scope.Documents.BindAsync(seed.Scope, again.Id, duplicate.MessageDbId, NoCancellation)).ShouldNotBeNull();
        rebound.SourceMessageId.ShouldBe(first.Admission.SourceMessageId);
        var repost = seed.Message with { MessageId = 601, SentAt = Now.AddMinutes(1) };
        var second = await RetainAsync(scope, seed, repost, 12, "second retained text");
        second.Admission.Id.ShouldNotBe(first.Admission.Id);
        second.Document.Id.ShouldNotBe(first.Document.Id);
        (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(2);
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(2);
        (await scope.Context.HealthDocuments.OrderBy(d => d.Id).Select(d => d.Text).ToListAsync())
            .ShouldBe(new[] { "first retained text", "second retained text" });
        (await scope.Context.HealthDocumentAdmissions.OrderBy(a => a.SentAt).Select(a => a.FirstUpdateId).ToListAsync())
            .ShouldBe(new long[] { 10, 12 });
    }

    [Fact]
    public async Task Conflicting_redelivery_cannot_replace_initial_caption_attachment_or_posted_date()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var retained = await RetainAsync(scope, seed, seed.Message, 10, "original body");
        var changed = seed.Message with
        {
            Text = "replacement caption", SentAt = Now.AddDays(1),
            Document = new("other-file", "other-unique", "other.txt", "text/plain", 3)
        };
        (await scope.Documents.AdmitAsync(seed.Scope, changed, 11, NoCancellation)).ShouldBeNull();
        var row = await scope.Context.HealthDocuments.AsNoTracking().SingleAsync();
        row.PostedAt.ShouldBe(seed.Message.SentAt);
        row.Caption.ShouldBe("synthetic caption");
        row.TelegramFileId.ShouldBe("synthetic-file");
        row.Text.ShouldBe("original body");
        row.Id.ShouldBe(retained.Document.Id);
    }

    [Fact]
    public async Task Unbound_orphan_does_not_create_document_or_consume_attempt_and_rotates_out_of_due_batch()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.BindAsync(seed.Scope, admission.Id, null, NoCancellation))!.SourceMessageId.ShouldBeNull();
        (await scope.Documents.TryClaimAsync(admission, NoCancellation)).ShouldBeNull();
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldHaveSingleItem().Id.ShouldBe(admission.Id);
        await scope.Documents.DeferAsync(admission, NoCancellation);
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.AttemptCount.ShouldBe(0);
        row.NextAttemptAt.ShouldBe(Now.AddSeconds(60));
        row.SourceMessageId.ShouldBeNull();
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
        (await scope.Messages.GetLastUpdateIdAsync(seed.Scope.TelegramBotId, NoCancellation)).ShouldBe(0);
    }

    [Fact]
    public async Task New_scope_recovers_after_source_and_offset_commit_without_replaying_telegram()
    {
        var seed = await SeedAsync();
        Guid admissionId;
        long? sourceId;
        await using (var beforeCrash = Open(seed.Scope.FamilyId))
        {
            admissionId = (await beforeCrash.Documents.AdmitAsync(seed.Scope, seed.Message, 42, NoCancellation))!.Id;
            var stored = await beforeCrash.Messages.StoreAsync(seed.Scope.TelegramBotId, 42, seed.Message, NoCancellation);
            stored.Outcome.ShouldBe(StoreOutcome.Stored);
            sourceId = stored.MessageDbId;
        }
        await using var recovered = Open(seed.Scope.FamilyId);
        (await recovered.Messages.GetLastUpdateIdAsync(seed.Scope.TelegramBotId, NoCancellation)).ShouldBe(42);
        var due = (await recovered.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldHaveSingleItem();
        due.Id.ShouldBe(admissionId);
        due.SourceMessageId.ShouldBeNull();
        var bound = (await recovered.Documents.BindAsync(seed.Scope, admissionId, null, NoCancellation)).ShouldNotBeNull();
        bound.SourceMessageId.ShouldBe(sourceId);
        var lease = (await recovered.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await recovered.Documents.FinishAsync(lease, Read("recovered body"), "read", null, null, NoCancellation)).ShouldBeTrue();
        await recovered.Documents.TryClaimDeliveryAsync(bound, true, NoCancellation);
        (await recovered.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        (await recovered.Context.Messages.CountAsync(m => m.Direction == MessageDirection.In)).ShouldBe(1);
        (await recovered.Context.HealthDocuments.AsNoTracking().SingleAsync()).Text.ShouldBe("recovered body");
        (await recovered.Context.Events.CountAsync()).ShouldBe(0);
        (await recovered.Context.PendingRecords.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Required_source_foreign_key_rejects_document_without_real_message()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        var invalid = new HealthDocument
        {
            FamilyId = seed.Scope.FamilyId, ProfileId = seed.Scope.ProfileId, AdmissionId = admission.Id,
            SourceMessageId = 999_999, TelegramFileId = "synthetic-file", PostedAt = Now, CreatedAt = Now, UpdatedAt = Now
        };
        scope.Context.HealthDocuments.Add(invalid);
        var error = await Should.ThrowAsync<DbUpdateException>(() => scope.Context.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        scope.Context.Entry(invalid).State = EntityState.Detached;
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
        (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Two_real_context_claims_create_one_metadata_row_and_one_lease_owner()
    {
        var seed = await SeedAsync();
        HealthDocumentAdmissionInfo bound;
        await using (var setup = Open(seed.Scope.FamilyId)) bound = await AdmitBindAsync(setup, seed, seed.Message, 10);
        var hold = new HoldUpdate("health_document_admissions");
        var arriving = new ObserveSourceLock();
        await using var first = Open(seed.Scope.FamilyId, hold);
        await using var second = Open(seed.Scope.FamilyId, arriving);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstClaim = first.Documents.TryClaimAsync(bound, timeout.Token);
        Task<HealthDocumentLease?>? secondClaim = null;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token);
            secondClaim = second.Documents.TryClaimAsync(bound, timeout.Token);
            await arriving.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); await Task.WhenAll(firstClaim, secondClaim ?? Task.FromResult<HealthDocumentLease?>(null)); }
        var winner = (await firstClaim).ShouldNotBeNull();
        (await secondClaim!).ShouldBeNull();
        await using var verify = Open(seed.Scope.FamilyId);
        (await verify.Context.HealthDocuments.CountAsync()).ShouldBe(1);
        var row = await verify.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.LeaseId.ShouldBe(winner.LeaseId);
        row.LeaseExpiresAt.ShouldBe(Now.AddMinutes(5));
        row.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Exact_lease_expiry_allows_takeover_and_stale_worker_cannot_finalize_renew_or_release_it()
    {
        var seed = await SeedAsync();
        await using var old = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(old, seed, seed.Message, 10);
        var first = (await old.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await old.Documents.TryBeginAttemptAsync(first, NoCancellation)).ShouldBe(1);
        _clock.UtcNow = Now.AddMinutes(5).AddTicks(-1);
        await using var replacement = Open(seed.Scope.FamilyId);
        (await replacement.Documents.TryClaimAsync(bound, NoCancellation)).ShouldBeNull();
        _clock.UtcNow = Now.AddMinutes(5);
        var next = (await replacement.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        next.LeaseId.ShouldNotBe(first.LeaseId);
        (await old.Documents.FinishAsync(first, Read("stale body"), "read", null, null, NoCancellation)).ShouldBeFalse();
        (await old.Documents.RenewAsync(first, NoCancellation)).ShouldBeFalse();
        await old.Documents.ReleaseAsync(first, NoCancellation);
        var owned = await replacement.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        owned.LeaseId.ShouldBe(next.LeaseId);
        owned.AttemptCount.ShouldBe(1);
        (await replacement.Documents.TryBeginAttemptAsync(next, NoCancellation)).ShouldBe(2);
        (await replacement.Documents.FinishAsync(next, Read("current body"), "read", null, null, NoCancellation)).ShouldBeTrue();
        var document = await replacement.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.Text.ShouldBe("current body");
        document.TextStatus.ShouldBe("read");
    }

    [Fact]
    public async Task Renewal_extends_only_current_lease_without_changing_attempt_or_text()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        _clock.UtcNow = Now.AddSeconds(30);
        (await scope.Documents.RenewAsync(lease, NoCancellation)).ShouldBeTrue();
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.LeaseId.ShouldBe(lease.LeaseId);
        row.LeaseExpiresAt.ShouldBe(Now.AddMinutes(5).AddSeconds(30));
        row.AttemptCount.ShouldBe(0);
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).Text.ShouldBeNull();
    }

    [Fact]
    public async Task Cancelled_finalization_keeps_metadata_and_release_preserves_consumed_attempt()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryBeginAttemptAsync(lease, NoCancellation)).ShouldBe(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => scope.Documents.FinishAsync(
            lease, Read("must not commit"), "read", null, null, cancelled.Token));
        await scope.Documents.ReleaseAsync(lease, NoCancellation);
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.LeaseId.ShouldBeNull();
        row.LeaseExpiresAt.ShouldBeNull();
        row.AttemptCount.ShouldBe(1);
        row.Status.ShouldBe("processing");
        var document = await scope.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.TextStatus.ShouldBe("processing");
        document.Text.ShouldBeNull();
        var next = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryBeginAttemptAsync(next, NoCancellation)).ShouldBe(2);
    }

    [Fact]
    public async Task Three_interrupted_download_attempts_remain_consumed_across_scopes()
    {
        var seed = await SeedAsync();
        HealthDocumentAdmissionInfo bound;
        await using (var setup = Open(seed.Scope.FamilyId)) bound = await AdmitBindAsync(setup, seed, seed.Message, 10);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var worker = Open(seed.Scope.FamilyId);
            var lease = (await worker.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
            (await worker.Documents.TryBeginAttemptAsync(lease, NoCancellation)).ShouldBe(attempt);
            await worker.Documents.ReleaseAsync(lease, NoCancellation);
        }
        await using var exhausted = Open(seed.Scope.FamilyId);
        var final = (await exhausted.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await exhausted.Documents.TryBeginAttemptAsync(final, NoCancellation)).ShouldBeNull();
        (await exhausted.Documents.FinishAsync(final, null, "failed", "interrupted", null, NoCancellation)).ShouldBeTrue();
        var admission = await exhausted.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        admission.AttemptCount.ShouldBe(3);
        admission.Status.ShouldBe("completed");
        var document = await exhausted.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.Text.ShouldBeNull();
        document.TextFailureReason.ShouldBe("interrupted");
        document.TextStatus.ShouldBe("failed");
    }

    [Fact]
    public async Task Retry_backoffs_refuse_early_claim_and_allow_exact_due_time_then_terminal_third_failure()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var first = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryBeginAttemptAsync(first, NoCancellation)).ShouldBe(1);
        (await scope.Documents.FinishAsync(first, null, "processing", "unavailable", Now.AddMinutes(1), NoCancellation)).ShouldBeTrue();
        _clock.UtcNow = Now.AddMinutes(1).AddTicks(-1);
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldBeNull();
        _clock.UtcNow = Now.AddMinutes(1);
        var second = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryBeginAttemptAsync(second, NoCancellation)).ShouldBe(2);
        (await scope.Documents.FinishAsync(second, null, "processing", "timeout", Now.AddMinutes(6), NoCancellation)).ShouldBeTrue();
        _clock.UtcNow = Now.AddMinutes(6).AddTicks(-1);
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        _clock.UtcNow = Now.AddMinutes(6);
        var third = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryBeginAttemptAsync(third, NoCancellation)).ShouldBe(3);
        (await scope.Documents.FinishAsync(third, null, "failed", "unavailable", null, NoCancellation)).ShouldBeTrue();
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.AttemptCount.ShouldBe(3);
        row.NextAttemptAt.ShouldBeNull();
        row.Status.ShouldBe("completed");
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).TextFailureReason.ShouldBe("unavailable");
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("bot")]
    public async Task Revoked_grant_stops_renewal_and_pauses_without_consuming_attempt(string revoked)
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        if (revoked == "member")
            await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        else if (revoked == "place")
            await scope.Context.Places.Where(p => p.BotId == seed.Scope.BotDbId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlaceStatus.Denied));
        else
            await scope.Context.Bots.Where(b => b.Id == seed.Scope.BotDbId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BotStatus.Disabled));
        (await scope.Documents.RenewAsync(lease, NoCancellation)).ShouldBeFalse();
        (await scope.Documents.IsAuthorizedAsync(bound, NoCancellation)).ShouldBeFalse();
        (await scope.Documents.TryBeginAttemptAsync(lease, NoCancellation)).ShouldBeNull();
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.Status.ShouldBe("paused");
        row.AttemptCount.ShouldBe(0);
        row.LeaseId.ShouldBeNull();
        row.NextAttemptAt.ShouldBe(Now.AddSeconds(60));
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).Text.ShouldBeNull();
    }

    [Fact]
    public async Task Renewed_approval_resumes_paused_work_and_revocation_does_not_erase_committed_text()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldBeNull();
        (await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync()).Status.ShouldBe("paused");
        await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Approved));
        _clock.UtcNow = Now.AddSeconds(60);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.FinishAsync(lease, Read("retained after revocation"), "read", null, null, NoCancellation)).ShouldBeTrue();
        await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        await scope.Documents.PauseAsync(bound, NoCancellation);
        var snapshot = await scope.Documents.GetContextAsync(seed.Scope, NoCancellation);
        snapshot.Documents.ShouldHaveSingleItem().Text.ShouldBe("retained after revocation");
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("supergroup", true)]
    [InlineData("private", false)]
    public async Task Anonymous_sender_requires_approved_group_place_and_private_sender_fails_closed(string chatType, bool admitted)
    {
        var seed = await SeedAsync(chatType: chatType, userId: null);
        await using var scope = Open(seed.Scope.FamilyId);
        var result = await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation);
        if (admitted)
        {
            result.ShouldNotBeNull().SenderUserId.ShouldBeNull();
            (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(1);
        }
        else
        {
            result.ShouldBeNull();
            (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(0);
        }
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("missing_file")]
    [InlineData("edit")]
    public async Task Denied_or_malformed_source_creates_no_admission(string reason)
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var message = seed.Message;
        if (reason == "member")
            await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        else if (reason == "place") message = message with { TopicId = 8 };
        else if (reason == "missing_file") message = message with { Document = null };
        else message = message with { IsEdit = true };
        (await scope.Documents.AdmitAsync(seed.Scope, message, 10, NoCancellation)).ShouldBeNull();
        (await scope.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(0);
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Unsupported_format_still_retains_metadata_without_any_download_attempt()
    {
        var seed = await SeedAsync();
        var message = seed.Message with { Document = new("synthetic-word", null, "synthetic.docx", null, null) };
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.FinishAsync(lease, null, "metadata_only", "unsupported_format", null, NoCancellation)).ShouldBeTrue();
        var document = await scope.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.FileName.ShouldBe("synthetic.docx");
        document.MimeType.ShouldBeNull();
        document.SizeBytes.ShouldBeNull();
        document.Caption.ShouldBe("synthetic caption");
        document.Text.ShouldBeNull();
        document.TextStatus.ShouldBe("metadata_only");
        document.TextFailureReason.ShouldBe("unsupported_format");
        (await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync()).AttemptCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("empty_read")]
    [InlineData("oversize_read")]
    [InlineData("unknown_reason")]
    [InlineData("retry_without_processing")]
    [InlineData("processing_without_retry")]
    public async Task Inconsistent_finalization_cannot_write_partial_text_or_release_lease(string invalid)
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        DocumentTextExtraction? result = invalid switch
        {
            "empty_read" => Read(" "),
            "oversize_read" => Read(new string('X', 200_001)),
            _ => null
        };
        var status = invalid is "empty_read" or "oversize_read" ? "read"
            : invalid == "processing_without_retry" ? "processing" : "failed";
        var reason = invalid is "empty_read" or "oversize_read" ? null
            : invalid == "unknown_reason" ? "raw parser diagnostic" : "unavailable";
        var retry = invalid == "retry_without_processing" ? (DateTimeOffset?)Now.AddMinutes(1) : null;
        await Should.ThrowAsync<ArgumentException>(() => scope.Documents.FinishAsync(
            lease, result, status, reason, retry, NoCancellation));
        var document = await scope.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.Text.ShouldBeNull();
        document.TextStatus.ShouldBe("processing");
        document.TextFailureReason.ShouldBeNull();
        var admission = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        admission.LeaseId.ShouldBe(lease.LeaseId);
        admission.Status.ShouldBe("processing");
    }

    [Fact]
    public async Task Delivery_dispositions_are_persisted_once_and_completed_notice_only_work_is_due()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.TryClaimDeliveryAsync(bound, true, NoCancellation)).ShouldBeTrue();
        (await scope.Documents.TryClaimDeliveryAsync(bound, true, NoCancellation)).ShouldBeFalse();
        (await scope.Documents.FinishAsync(lease, null, "metadata_only", "no_readable_text", null, NoCancellation)).ShouldBeTrue();
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldHaveSingleItem().Id.ShouldBe(bound.Id);
        (await scope.Documents.TryClaimDeliveryAsync(bound, false, NoCancellation)).ShouldBeTrue();
        (await scope.Documents.TryClaimDeliveryAsync(bound, false, NoCancellation)).ShouldBeFalse();
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        (await scope.Documents.HasAcknowledgedDocumentAsync(seed.Scope, seed.Message.ChatId, seed.Message.MessageId, NoCancellation)).ShouldBeTrue();
        var row = await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        row.ReactionAttempted.ShouldBeTrue();
        row.NoticeAttempted.ShouldBeTrue();
        row.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Listing_returns_ten_newest_by_immutable_posted_date_then_id_without_document_text()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var ids = new List<long>();
        for (var i = 0; i < 12; i++)
        {
            var retained = await RetainAsync(scope, seed, seed.Message with { MessageId = 500 + i }, 10 + i, $"body {i}");
            ids.Add(retained.Document.Id);
        }
        var latest = await scope.Documents.GetLatestAsync(seed.Scope, NoCancellation);
        latest.Select(d => d.Id).ShouldBe(ids.AsEnumerable().Reverse().Take(10).ToArray());
        latest.All(d => d.PostedAt == Now).ShouldBeTrue();
        latest.Select(d => d.Text).ShouldBe(Enumerable.Repeat<string?>(null, 10).ToArray());
        latest.Select(d => d.TextStatus).ShouldBe(Enumerable.Repeat("read", 10).ToArray());
        var document = (await scope.Documents.GetDocumentAsync(seed.Scope,
            (await scope.Context.HealthDocumentAdmissions.OrderBy(a => a.TelegramMessageId).FirstAsync()).Id, NoCancellation)).ShouldNotBeNull();
        document.Id.ShouldBe(ids[0]);
        document.Text.ShouldBeNull();
    }

    [Fact]
    public async Task Recovery_batch_is_ten_and_deferring_orphan_advances_the_next_due_source()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admissions = new List<HealthDocumentAdmissionInfo>();
        for (var i = 0; i < 11; i++)
        {
            _clock.UtcNow = Now.AddTicks(i * 10);
            admissions.Add((await scope.Documents.AdmitAsync(seed.Scope,
                seed.Message with { MessageId = 500 + i }, 10 + i, NoCancellation)).ShouldNotBeNull());
        }
        var due = await scope.Documents.GetDueAsync(seed.Scope, NoCancellation);
        due.Select(a => a.Id).ShouldBe(admissions.Take(10).Select(a => a.Id).ToArray());
        await scope.Documents.DeferAsync(due[0], NoCancellation);
        (await scope.Documents.GetDueAsync(seed.Scope, NoCancellation)).Select(a => a.Id)
            .ShouldBe(admissions.Skip(1).Select(a => a.Id).ToArray());
        (await scope.Context.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Context_inventory_has_all_ages_but_fetches_only_bounded_newest_text_in_server_projection()
    {
        var seed = await SeedAsync();
        var capture = new CaptureReads();
        await using var scope = Open(seed.Scope.FamilyId, capture);
        var old = await RetainAsync(scope, seed, seed.Message with
            { MessageId = 500, SentAt = Now.AddDays(-400), Text = "old caption" }, 10, "old body");
        var middle = await RetainAsync(scope, seed, seed.Message with
            { MessageId = 501, SentAt = Now.AddDays(-100) }, 11, "middle body");
        var newest = await RetainAsync(scope, seed, seed.Message with { MessageId = 502 }, 12, new string('N', 200_000));
        capture.Commands.Clear();
        var snapshot = await scope.Documents.GetContextAsync(seed.Scope, NoCancellation);
        snapshot.Documents.Select(d => d.Id).ShouldBe(new[] { newest.Document.Id, middle.Document.Id, old.Document.Id });
        snapshot.Documents[0].Text.ShouldBe(new string('N', 20_001));
        snapshot.Documents[0].ContextTextTruncated.ShouldBeTrue();
        snapshot.Documents[0].TextTruncated.ShouldBeFalse();
        snapshot.Documents[1].Text.ShouldBeNull();
        snapshot.Documents[2].Text.ShouldBeNull();
        snapshot.Documents[2].Caption.ShouldBe("old caption");
        snapshot.Documents[2].PostedAt.ShouldBe(Now.AddDays(-400));
        capture.Commands.Count(c => c.Contains("substring", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public async Task Context_retains_complete_newest_text_then_only_remaining_next_prefix()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        await RetainAsync(scope, seed, seed.Message with { MessageId = 500, SentAt = Now.AddDays(-2) }, 10, new string('O', 6_000));
        await RetainAsync(scope, seed, seed.Message with { MessageId = 501, SentAt = Now.AddDays(-1) }, 11, new string('M', 19_000));
        await RetainAsync(scope, seed, seed.Message with { MessageId = 502 }, 12, new string('N', 5_000));
        var rows = (await scope.Documents.GetContextAsync(seed.Scope, NoCancellation)).Documents;
        rows.Count.ShouldBe(3);
        rows[0].Text.ShouldBe(new string('N', 5_000));
        rows[0].ContextTextTruncated.ShouldBeFalse();
        rows[1].Text.ShouldBe(new string('M', 15_001));
        rows[1].ContextTextTruncated.ShouldBeTrue();
        rows[2].Text.ShouldBeNull();
    }

    [Fact]
    public async Task Context_prefix_preserves_surrogate_pair_and_stored_cap_marker_is_independent()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var body = new string('A', 19_999) + "😀" + new string('B', 100);
        await RetainAsync(scope, seed, seed.Message, 10, body, storedTruncated: true);
        var document = (await scope.Documents.GetContextAsync(seed.Scope, NoCancellation)).Documents.ShouldHaveSingleItem();
        document.Text.ShouldBe(new string('A', 19_999) + "😀");
        document.TextTruncated.ShouldBeTrue();
        document.ContextTextTruncated.ShouldBeTrue();
        document.Text!.Length.ShouldBe(20_001);
        char.IsHighSurrogate(document.Text[^2]).ShouldBeTrue();
        char.IsLowSurrogate(document.Text[^1]).ShouldBeTrue();
    }

    [Fact]
    public async Task Deleted_and_other_profile_documents_do_not_enter_library_or_context()
    {
        var first = await SeedAsync();
        var other = await SeedAsync(telegramBotId: 1002, existingFamilyId: first.Scope.FamilyId);
        await using var scope = Open(first.Scope.FamilyId);
        var retained = await RetainAsync(scope, first, first.Message, 10, "first body");
        await using (var sibling = Open(other.Scope.FamilyId))
            await RetainAsync(sibling, other, other.Message, 10, "sibling body");
        (await scope.Documents.GetContextAsync(first.Scope, NoCancellation)).Documents.ShouldHaveSingleItem().Id.ShouldBe(retained.Document.Id);
        (await scope.Documents.DeleteSourceAsync(first.Scope, first.Message.ChatId, first.Message.TopicId,
            first.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeTrue();
        (await scope.Documents.GetLatestAsync(first.Scope, NoCancellation)).ShouldBeEmpty();
        (await scope.Documents.GetContextAsync(first.Scope, NoCancellation)).Documents.ShouldBeEmpty();
        await using var verifySibling = Open(other.Scope.FamilyId);
        (await verifySibling.Documents.GetContextAsync(other.Scope, NoCancellation)).Documents.ShouldHaveSingleItem().Text.ShouldBe("sibling body");
    }

    [Fact]
    public async Task Finalization_failure_rolls_back_text_and_retains_exact_lease_for_retry()
    {
        var seed = await SeedAsync();
        var fail = new FailUpdate("health_document_admissions");
        await using var scope = Open(seed.Scope.FamilyId, fail);
        var bound = await AdmitBindAsync(scope, seed, seed.Message, 10);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        fail.Armed = true;
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Documents.FinishAsync(
            lease, Read("not committed"), "read", null, null, NoCancellation));
        await using var verify = Open(seed.Scope.FamilyId);
        var document = await verify.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.Text.ShouldBeNull();
        document.TextStatus.ShouldBe("processing");
        var admission = await verify.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        admission.LeaseId.ShouldBe(lease.LeaseId);
        admission.Status.ShouldBe("processing");
        (await scope.Documents.FinishAsync(lease, Read("retry committed"), "read", null, null, NoCancellation)).ShouldBeTrue();
        (await verify.Context.HealthDocuments.AsNoTracking().SingleAsync()).Text.ShouldBe("retry committed");
    }

    [Fact]
    public async Task Source_deletion_atomically_removes_document_caption_events_pending_and_reaction()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var retained = await RetainAsync(scope, seed, seed.Message with { SentAt = Now.AddDays(-400) }, 10, "old retained body");
        var source = Source(retained.Admission);
        var saved = (await scope.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, source,
            [Reading()], NoCancellation)).ShouldHaveSingleItem();
        var pendingId = await scope.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            Pending(retained.Admission), NoCancellation);
        await scope.Pending.SetPromptMessageAsync(seed.Scope.FamilyId, pendingId, 701, NoCancellation);
        var deletion = await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation);
        deletion.DocumentDeleted.ShouldBeTrue();
        deletion.DeletedEvents.Events.ShouldHaveSingleItem().Id.ShouldBe(saved.Id);
        deletion.DeletedEvents.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(seed.Message.ChatId, seed.Message.MessageId) });
        deletion.ClosedPending.ShouldHaveSingleItem().PromptMessageId.ShouldBe(701);
        deletion.ClosedPending[0].Status.ShouldBe(PendingRecordStatuses.Declined);
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBe(Now);
        (await scope.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync()).Status.ShouldBe("deleted");
        (await scope.Context.Events.AsNoTracking().SingleAsync()).DeleteReason.ShouldBe(EventDeleteReasons.Del);
        (await scope.Context.PendingRecords.AsNoTracking().SingleAsync()).Status.ShouldBe(PendingRecordStatuses.Declined);
        (await scope.Documents.HasAcknowledgedDocumentAsync(seed.Scope, seed.Message.ChatId, seed.Message.MessageId, NoCancellation)).ShouldBeFalse();
        (await scope.Documents.GetContextAsync(seed.Scope, NoCancellation)).Documents.ShouldBeEmpty();
        (await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeFalse();
        (await scope.Context.Events.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Failure_after_document_and_event_delete_rolls_back_all_source_effects()
    {
        var seed = await SeedAsync();
        Retained retained;
        long pendingId;
        await using (var setup = Open(seed.Scope.FamilyId))
        {
            retained = await RetainAsync(setup, seed, seed.Message, 10, "kept after rollback");
            await setup.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, Source(retained.Admission), [Reading()], NoCancellation);
            pendingId = await setup.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, Pending(retained.Admission), NoCancellation);
        }
        var fail = new FailUpdate("pending_records") { Armed = true };
        await using var deleting = Open(seed.Scope.FamilyId, fail);
        await Should.ThrowAsync<InvalidOperationException>(() => deleting.Documents.DeleteSourceAsync(seed.Scope,
            seed.Message.ChatId, seed.Message.TopicId, seed.Message.MessageId, 222, NoCancellation));
        await using var verify = Open(seed.Scope.FamilyId);
        var document = await verify.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.DeletedAt.ShouldBeNull();
        document.Text.ShouldBe("kept after rollback");
        var admission = await verify.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
        admission.DeletedAt.ShouldBeNull();
        admission.Status.ShouldBe("completed");
        (await verify.Context.Events.AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();
        (await verify.Pending.FindAsync(seed.Scope.FamilyId, pendingId, NoCancellation))!.Status.ShouldBe(PendingRecordStatuses.Pending);
        (await verify.Documents.GetContextAsync(seed.Scope, NoCancellation)).Documents.ShouldHaveSingleItem().Id.ShouldBe(retained.Document.Id);
    }

    [Fact]
    public async Task Acceptance_holding_source_lock_commits_then_waiting_delete_removes_its_event()
    {
        var seed = await SeedAsync();
        Retained retained;
        long pendingId;
        await using (var setup = Open(seed.Scope.FamilyId))
        {
            retained = await RetainAsync(setup, seed, seed.Message, 10, "synthetic body");
            pendingId = await setup.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, Pending(retained.Admission), NoCancellation);
        }
        var hold = new HoldUpdate("pending_records");
        var arriving = new ObserveSourceLock();
        await using var accepting = Open(seed.Scope.FamilyId, hold);
        await using var deleting = Open(seed.Scope.FamilyId, arriving);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        IReadOnlyList<HealthEventInfo> acceptedEvents = [];
        var accepted = accepting.Pending.TryResolveAsync(seed.Scope.FamilyId, pendingId, PendingRecordStatuses.Accepted, 222,
            async ct => acceptedEvents = await accepting.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
                Source(retained.Admission), [Reading()], ct), timeout.Token);
        Task<HealthDocumentSourceDeletion>? deletion = null;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token);
            deletion = deleting.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
                seed.Message.MessageId, 222, timeout.Token);
            await arriving.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); await Task.WhenAll(accepted, deletion ?? Task.FromResult(HealthDocumentSourceDeletion.None)); }
        (await accepted).ShouldBeTrue();
        var result = await deletion!;
        result.DeletedEvents.Events.Select(e => e.Id).ShouldBe(acceptedEvents.Select(e => e.Id).ToArray());
        result.DeletedEvents.Events.Count.ShouldBe(1);
        await using var verify = Open(seed.Scope.FamilyId);
        (await verify.Context.Events.CountAsync(e => e.DeletedAt == null)).ShouldBe(0);
        (await verify.Context.Events.CountAsync()).ShouldBe(1);
        (await verify.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBe(Now);
        (await verify.Pending.FindAsync(seed.Scope.FamilyId, pendingId, NoCancellation))!.Status.ShouldBe(PendingRecordStatuses.Accepted);
    }

    [Fact]
    public async Task Delete_holding_source_lock_closes_pending_before_waiting_acceptance_can_create_events()
    {
        var seed = await SeedAsync();
        Retained retained;
        long pendingId;
        await using (var setup = Open(seed.Scope.FamilyId))
        {
            retained = await RetainAsync(setup, seed, seed.Message, 10, "synthetic body");
            pendingId = await setup.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, Pending(retained.Admission), NoCancellation);
        }
        var hold = new HoldUpdate("health_document_admissions");
        var arriving = new ObserveSourceLock();
        await using var deleting = Open(seed.Scope.FamilyId, hold);
        await using var accepting = Open(seed.Scope.FamilyId, arriving);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var deletion = deleting.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, timeout.Token);
        Task<bool>? accepted = null;
        var callbacks = 0;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token);
            accepted = accepting.Pending.TryResolveAsync(seed.Scope.FamilyId, pendingId, PendingRecordStatuses.Accepted, 222,
                async ct =>
                {
                    callbacks++;
                    await accepting.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
                        Source(retained.Admission), [Reading()], ct);
                }, timeout.Token);
            await arriving.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); await Task.WhenAll(deletion, accepted ?? Task.FromResult(false)); }
        (await deletion).ClosedPending.ShouldHaveSingleItem().Id.ShouldBe(pendingId);
        (await accepted!).ShouldBeFalse();
        callbacks.ShouldBe(0);
        await using var verify = Open(seed.Scope.FamilyId);
        (await verify.Context.Events.CountAsync()).ShouldBe(0);
        (await verify.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBe(Now);
        (await verify.Pending.FindAsync(seed.Scope.FamilyId, pendingId, NoCancellation))!.Status.ShouldBe(PendingRecordStatuses.Declined);
    }

    [Fact]
    public async Task Source_delete_during_processing_refuses_late_text_and_redelivery_cannot_resurrect_it()
    {
        var seed = await SeedAsync();
        await using var processing = Open(seed.Scope.FamilyId);
        var bound = await AdmitBindAsync(processing, seed, seed.Message, 10);
        var lease = (await processing.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        await using (var deleting = Open(seed.Scope.FamilyId))
            (await deleting.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
                seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeTrue();
        (await processing.Documents.FinishAsync(lease, Read("late body"), "read", null, null, NoCancellation)).ShouldBeFalse();
        var again = (await processing.Documents.AdmitAsync(seed.Scope, seed.Message, 11, NoCancellation)).ShouldNotBeNull();
        again.Id.ShouldBe(bound.Id);
        again.Status.ShouldBe("deleted");
        (await processing.Documents.TryClaimAsync(again, NoCancellation)).ShouldBeNull();
        (await processing.Documents.TryClaimDeliveryAsync(again, true, NoCancellation)).ShouldBeFalse();
        (await processing.Documents.GetDueAsync(seed.Scope, NoCancellation)).ShouldBeEmpty();
        var document = await processing.Context.HealthDocuments.AsNoTracking().SingleAsync();
        document.DeletedAt.ShouldBe(Now);
        document.Text.ShouldBeNull();
        (await processing.Context.HealthDocuments.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Tombstoned_document_source_refuses_late_event_and_pending_creation()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var retained = await RetainAsync(scope, seed, seed.Message, 10, "synthetic body");
        await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            Source(retained.Admission), [Reading()], NoCancellation));
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            Pending(retained.Admission), NoCancellation));
        (await scope.Context.Events.CountAsync()).ShouldBe(0);
        (await scope.Context.PendingRecords.CountAsync()).ShouldBe(0);
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Event_only_delete_retains_document_reaction_then_source_delete_clears_it()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var retained = await RetainAsync(scope, seed, seed.Message, 10, "synthetic body");
        var saved = (await scope.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            Source(retained.Admission), [Reading()], NoCancellation)).ShouldHaveSingleItem();
        var eventOnly = await scope.Events.DeleteByIdAsync(seed.Scope.FamilyId, seed.Scope.ProfileId, saved.Id,
            EventDeleteReasons.Del, NoCancellation);
        eventOnly.Events.ShouldHaveSingleItem().Id.ShouldBe(saved.Id);
        eventOnly.MessagesWithoutEvents.ShouldBeEmpty();
        (await scope.Documents.HasAcknowledgedDocumentAsync(seed.Scope, seed.Message.ChatId, seed.Message.MessageId, NoCancellation)).ShouldBeTrue();
        var wholeSource = await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation);
        wholeSource.DeletedEvents.Events.ShouldBeEmpty();
        wholeSource.DeletedEvents.MessagesWithoutEvents.ShouldBe(new[] { new MessageRef(seed.Message.ChatId, seed.Message.MessageId) });
        (await scope.Documents.HasAcknowledgedDocumentAsync(seed.Scope, seed.Message.ChatId, seed.Message.MessageId, NoCancellation)).ShouldBeFalse();
    }

    [Fact]
    public async Task Unbound_deletion_does_not_touch_unrelated_null_source_event_or_pending()
    {
        var seed = await SeedAsync();
        await using var scope = Open(seed.Scope.FamilyId);
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, seed.Message, 10, NoCancellation)).ShouldNotBeNull();
        var manualEvent = (await scope.Events.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            new(null, seed.Scope.TelegramBotId, seed.Message.ChatId, seed.Message.TopicId, 111), [Reading()], NoCancellation)).ShouldHaveSingleItem();
        var pendingId = await scope.Pending.AddAsync(seed.Scope.FamilyId, seed.Scope.ProfileId,
            Pending(admission), NoCancellation);
        var deleted = await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation);
        deleted.DocumentDeleted.ShouldBeTrue();
        deleted.DeletedEvents.Events.ShouldBeEmpty();
        deleted.ClosedPending.ShouldBeEmpty();
        (await scope.Context.Events.AsNoTracking().SingleAsync()).Id.ShouldBe(manualEvent.Id);
        (await scope.Context.Events.AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();
        (await scope.Pending.FindAsync(seed.Scope.FamilyId, pendingId, NoCancellation))!.Status.ShouldBe(PendingRecordStatuses.Pending);
        (await scope.Documents.TryClaimAsync(admission, NoCancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task Source_delete_refuses_wrong_topic_chat_and_sibling_scope_but_ignores_original_sender_revocation()
    {
        var seed = await SeedAsync();
        var sibling = await SeedAsync(telegramBotId: 1002, existingFamilyId: seed.Scope.FamilyId);
        await using var scope = Open(seed.Scope.FamilyId);
        await RetainAsync(scope, seed, seed.Message, 10, "retained body");
        (await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, null,
            seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeFalse();
        (await scope.Documents.DeleteSourceAsync(seed.Scope, -200, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeFalse();
        await using (var other = Open(sibling.Scope.FamilyId))
            (await other.Documents.DeleteSourceAsync(sibling.Scope, seed.Message.ChatId, seed.Message.TopicId,
                seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeFalse();
        await scope.Context.FamilyMembers.Where(m => m.FamilyId == seed.Scope.FamilyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        (await scope.Documents.DeleteSourceAsync(seed.Scope, seed.Message.ChatId, seed.Message.TopicId,
            seed.Message.MessageId, 222, NoCancellation)).Changed.ShouldBeTrue();
        (await scope.Context.HealthDocuments.AsNoTracking().SingleAsync()).DeletedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("admission", true, 4)]
    [InlineData("storage", true, 4)]
    [InlineData("storage", false, 2)]
    public async Task Real_poller_preserves_valid_unsupported_file_offset_beyond_poison_cap_and_malformed_source_uses_cap(
        string boundary, bool validMetadata, int expectedFailures)
    {
        var seed = await SeedAsync();
        var message = seed.Message with
        {
            Document = validMetadata ? new("synthetic-word", null, "synthetic.docx", null, null)
                : new("", null, "synthetic.docx", null, null),
            Text = null
        };
        var failure = new FailInsert(boundary == "admission" ? "health_document_admissions" : "messages", 4);
        var bot = new ReceivingBot(seed.Scope.BotDbId, seed.Scope.TelegramBotId, "synthetic_bot", seed.Scope.FamilyId, "health");
        var telegram = new OffsetSignalTelegram(bot);
        telegram.Inner.EnqueueUpdate(new(1, message));
        var services = new ServiceCollection();
        services.AddScoped(_ => Open(seed.Scope.FamilyId, failure));
        services.AddScoped<IMessageStore>(p => p.GetRequiredService<TestScope>().Messages);
        services.AddScoped(p =>
        {
            var session = p.GetRequiredService<TestScope>();
            var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token" });
            var approvals = new ApprovalService(session.Context, new FixedClientFactory(telegram), options, _clock);
            return new UpdateHandler(session.Messages, approvals, session.Current, new UnusedManager(), new UnusedGeneral(),
                new IntakeBridge(session.Documents, seed.Scope), options, new BuildInfo("abcdef1", null, Now),
                _clock, NullLogger<UpdateHandler>.Instance);
        });
        using var provider = services.BuildServiceProvider();
        var worker = new BotPollingWorker(bot, telegram, [UpdateKind.Message],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, PoisonUpdateFailureCap: 2),
            new PollingHealth(), _clock, NullLogger.Instance, "test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = worker.RunAsync(timeout.Token);
        try { await telegram.OffsetAdvanced.Task.WaitAsync(timeout.Token); }
        finally { await timeout.CancelAsync(); await running; }
        failure.Failures.ShouldBe(expectedFailures);
        telegram.Inner.RequestedOffsets.Take(expectedFailures + (validMetadata ? 1 : 0))
            .ShouldBe(Enumerable.Repeat(1L, expectedFailures + (validMetadata ? 1 : 0)).ToArray());
        await using var verify = Open(seed.Scope.FamilyId);
        (await verify.Messages.GetLastUpdateIdAsync(seed.Scope.TelegramBotId, NoCancellation)).ShouldBe(1);
        (await verify.Context.Messages.CountAsync(m => m.Direction == MessageDirection.In)).ShouldBe(validMetadata ? 1 : 0);
        (await verify.Context.HealthDocumentAdmissions.CountAsync()).ShouldBe(validMetadata ? 1 : 0);
        if (validMetadata)
        {
            var admission = await verify.Context.HealthDocumentAdmissions.AsNoTracking().SingleAsync();
            admission.FileName.ShouldBe("synthetic.docx");
            admission.SourceMessageId.ShouldBe((await verify.Context.Messages.AsNoTracking().SingleAsync()).Id);
            admission.AttemptCount.ShouldBe(0);
        }
        telegram.Inner.DownloadedFiles.ShouldBeEmpty();
        (await verify.Context.Events.CountAsync()).ShouldBe(0);
        (await verify.Context.PendingRecords.CountAsync()).ShouldBe(0);
    }

    private async Task<Seed> SeedAsync(
        long telegramBotId = 1001, string chatType = "supergroup", long? userId = 111, long? existingFamilyId = null)
    {
        Family family;
        if (existingFamilyId is { } familyId)
            family = await Db.Families.AsNoTracking().SingleAsync(f => f.Id == familyId);
        else
        {
            family = new Family { Name = "synthetic family", CreatedAt = Now };
            Db.Families.Add(family);
            await Db.SaveChangesAsync();
        }
        var bot = new Bot { FamilyId = family.Id, TelegramBotId = telegramBotId, Username = "synthetic_bot",
            Role = "health", Status = BotStatus.Active, CreatedAt = Now };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        var profile = new HealthProfile { FamilyId = family.Id, BotId = bot.Id, CreatedAt = Now, UpdatedAt = Now };
        Db.HealthProfiles.Add(profile);
        if (userId is { } member && !await Db.FamilyMembers.AnyAsync(m => m.FamilyId == family.Id && m.TelegramUserId == member))
            Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = member, DisplayName = "synthetic member",
                Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = Now, UpdatedAt = Now });
        var chat = chatType == "private" ? userId ?? 111 : -100;
        int? topic = chatType == "private" ? null : 7;
        if (chatType != "private") Db.Places.Add(new Place { BotId = bot.Id, ChatId = chat, TopicId = topic,
            Title = "synthetic place", Status = PlaceStatus.Approved, CreatedAt = Now });
        await Db.SaveChangesAsync();
        var message = new IncomingMessage(chat, chatType, null, topic, 500, userId, null, "synthetic caption",
            MessageKind.Document, false, Now, null, null, "{}", null, null,
            new DocumentAttachment("synthetic-file", "synthetic-unique", "synthetic.txt", "text/plain", 20));
        return new Seed(new(family.Id, profile.Id, bot.Id, telegramBotId), message);
    }

    private TestScope Open(long? familyId, params IInterceptor[] interceptors)
    {
        var current = new CurrentFamily();
        if (familyId is { } family) current.Set(family);
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        options.AddInterceptors(interceptors);
        var context = new AssistantDbContext(options.Options, current);
        return new TestScope(context, new(context, current, _clock), new(context, current, _clock),
            new(context, current, _clock), new(context, _clock, NullLogger<MessageStore>.Instance), current);
    }

    private static async Task<HealthDocumentAdmissionInfo> AdmitBindAsync(
        TestScope scope, Seed seed, IncomingMessage message, long updateId)
    {
        var admission = (await scope.Documents.AdmitAsync(seed.Scope, message, updateId, NoCancellation)).ShouldNotBeNull();
        var stored = await scope.Messages.StoreAsync(seed.Scope.TelegramBotId, updateId, message, NoCancellation);
        stored.Outcome.ShouldBe(StoreOutcome.Stored);
        var bound = (await scope.Documents.BindAsync(seed.Scope, admission.Id, stored.MessageDbId, NoCancellation)).ShouldNotBeNull();
        bound.SourceMessageId.ShouldBe(stored.MessageDbId);
        return bound;
    }

    private static async Task<Retained> RetainAsync(
        TestScope scope, Seed seed, IncomingMessage message, long updateId, string text, bool storedTruncated = false)
    {
        var bound = await AdmitBindAsync(scope, seed, message, updateId);
        var lease = (await scope.Documents.TryClaimAsync(bound, NoCancellation)).ShouldNotBeNull();
        (await scope.Documents.FinishAsync(lease, Read(text, storedTruncated), "read", null, null, NoCancellation)).ShouldBeTrue();
        (await scope.Documents.TryClaimDeliveryAsync(bound, true, NoCancellation)).ShouldBeTrue();
        var document = (await scope.Documents.GetDocumentAsync(seed.Scope, bound.Id, NoCancellation)).ShouldNotBeNull();
        return new Retained(bound, document);
    }

    private static DocumentTextExtraction Read(string text, bool truncated = false) => new(text, null, truncated, false);
    private static NewHealthEvent Reading() => new("weight", Now, "message", "{\"kg\":60}");
    private static HealthEventSource Source(HealthDocumentAdmissionInfo admission) => new(
        admission.SourceMessageId, admission.Scope.TelegramBotId, admission.ChatId, admission.TopicId, admission.SenderUserId);
    private static NewPendingRecord Pending(HealthDocumentAdmissionInfo admission) => new(
        admission.SourceMessageId, admission.Scope.TelegramBotId, admission.ChatId, admission.TopicId,
        admission.TelegramMessageId, admission.SenderUserId, [Reading()], []);

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } = Now; }
    private sealed record Seed(HealthDocumentScope Scope, IncomingMessage Message);
    private sealed record Retained(HealthDocumentAdmissionInfo Admission, HealthDocumentInfo Document);
    private sealed record TestScope(
        AssistantDbContext Context, HealthDocumentStore Documents, PendingRecordStore Pending,
        EventStore Events, MessageStore Messages, CurrentFamily Current) : IAsyncDisposable, IDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
        public void Dispose() => Context.Dispose();
    }

    private sealed class FailUpdate(string table) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && IsUpdate(command, table))
            {
                Armed = false;
                throw new InvalidOperationException("synthetic SQL write failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HoldUpdate(string table) : DbCommandInterceptor
    {
        private int _entered;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult(true);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsUpdate(command, table) && Interlocked.CompareExchange(ref _entered, 1, 0) == 0)
            {
                Entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObserveSourceLock : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)) Entered.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CaptureReads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailInsert(string table, int remaining) : DbCommandInterceptor
    {
        public int Failures { get; private set; }
        private void Fail(DbCommand command)
        {
            if (remaining > 0 && command.CommandText.Replace("\"", "", StringComparison.Ordinal)
                    .Contains("INSERT INTO " + table, StringComparison.OrdinalIgnoreCase))
            {
                remaining--;
                Failures++;
                throw new IOException("synthetic SQL transport interruption");
            }
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Fail(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Fail(command); return ValueTask.FromResult(result); }
    }

    // The narrow bridge deliberately omits extraction/caption behavior. Real UpdateHandler,
    // ApprovalService, MessageStore, HealthDocumentStore and BotPollingWorker own the SQL/offset
    // behavior under test; Application tests cover the full Health assistant separately.
    private sealed class IntakeBridge(HealthDocumentStore documents, HealthDocumentScope scope) : IHealthAssistant
    {
        public Task<HealthDocumentAdmissionInfo?> AdmitDocumentAsync(ReceivingBot bot, IncomingMessage message,
            long updateId, CancellationToken token) => documents.AdmitAsync(scope, message, updateId, token);
        public Task ResumeDocumentsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken token) => Task.CompletedTask;
        public async Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
            StoreResult result, CancellationToken token, bool replyToAll = false)
        {
            var admission = await documents.FindAsync(scope, message.ChatId, message.TopicId, message.MessageId, token);
            if (admission is not null) await documents.BindAsync(scope, admission.Id, result.MessageDbId, token);
        }
        public Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback,
            CancellationToken token) => Task.CompletedTask;
    }

    private sealed class FixedClientFactory(ITelegramClient client) : ITelegramClientFactory
    { public ITelegramClient Create(string token) => client; }
    private sealed class UnusedManager : IManagerUpdateHandler
    { public Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingUpdate update, CancellationToken token) => Task.CompletedTask; }
    private sealed class UnusedGeneral : IGeneralAssistant
    {
        public Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, StoreResult result,
            CancellationToken token, bool replyToAll = false) => Task.CompletedTask;
    }

    private sealed class OffsetSignalTelegram(ReceivingBot bot) : ITelegramClient
    {
        private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Assistant.IntegrationTests.Host.FakeTelegramClient Inner { get; } = new();
        public TaskCompletionSource<bool> OffsetAdvanced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BotIdentity> GetMeAsync(CancellationToken token) => Task.FromResult(new BotIdentity(bot.TelegramBotId, bot.Username));
        public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds,
            IReadOnlyList<UpdateKind> allowed, CancellationToken token)
        {
            if (offset >= 2)
            {
                OffsetAdvanced.TrySetResult(true);
                await _stopped.Task.WaitAsync(token);
                return [];
            }
            return await Inner.GetUpdatesAsync(offset, timeoutSeconds, allowed, token);
        }
        public Task<long> DownloadFileAsync(string file, Stream destination, long maxBytes, CancellationToken token)
            => Inner.DownloadFileAsync(file, destination, maxBytes, token);
        public Task<int> SendTextAsync(long chat, int? topic, string text, int? reply, CancellationToken token)
            => Inner.SendTextAsync(chat, topic, text, reply, token);
        public Task SendChatActionAsync(long chat, int? topic, string action, CancellationToken token)
            => Inner.SendChatActionAsync(chat, topic, action, token);
        public Task SetReactionAsync(long chat, int message, string? emoji, CancellationToken token)
            => Inner.SetReactionAsync(chat, message, emoji, token);
        public Task<int> SendTextWithButtonsAsync(long chat, int? topic, string text, IReadOnlyList<InlineButton> buttons,
            int? reply, CancellationToken token) => Inner.SendTextWithButtonsAsync(chat, topic, text, buttons, reply, token);
        public Task EditMessageButtonsAsync(long chat, int message, IReadOnlyList<InlineButton> buttons, CancellationToken token)
            => Inner.EditMessageButtonsAsync(chat, message, buttons, token);
        public Task EditMessageTextAsync(long chat, int message, string text, CancellationToken token)
            => Inner.EditMessageTextAsync(chat, message, text, token);
        public Task AnswerCallbackAsync(string id, string? text, CancellationToken token) => Inner.AnswerCallbackAsync(id, text, token);
        public Task<string> GetManagedBotTokenAsync(long user, CancellationToken token) => Inner.GetManagedBotTokenAsync(user, token);
    }

    private static bool IsUpdate(DbCommand command, string table) =>
        command.CommandText.TrimStart().StartsWith("UPDATE " + table, StringComparison.OrdinalIgnoreCase)
        || command.CommandText.TrimStart().StartsWith("UPDATE \"" + table + "\"", StringComparison.OrdinalIgnoreCase);
}
