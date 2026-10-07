using System.Text;
using Assistant.Application.Common;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Health.Documents;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Application.Health;

public sealed class HealthDocumentProcessorTests
{
    private static readonly DateTimeOffset Now = new(2030, 4, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly HealthDocumentScope Scope = new(42, 1, 10, 999);
    private readonly FakeHealthDocumentStore _store = new();
    private readonly FakeTelegramClient _telegram = new();
    private readonly MutableClock _clock = new(Now);
    private readonly ManualLeaseKeeper _leases = new();

    private HealthDocumentAdmissionInfo Admission(string name = "synthetic.txt", long? size = null, int attempts = 0) =>
        _store.Admission = new(Guid.NewGuid(), Scope, -100, 7, "supergroup", 33, 111, Now,
            new DocumentAttachment("synthetic-file", null, name, null, size), null, 22, "admitted", attempts, null, false, false);
    private HealthDocumentProcessor Processor(IDocumentTextExtractor? extractor = null) => new(_store,
        extractor ?? new DocumentTextExtractor(), _leases, new HealthDocumentExecutionGate(_clock), _clock,
        NullLogger<HealthDocumentProcessor>.Instance);

    [Fact]
    public async Task Readable_file_retains_exact_text_and_one_reaction_without_a_generic_reply()
    {
        var admission = Admission();
        _telegram.Files[admission.Attachment.FileId] = Encoding.UTF8.GetBytes("synthetic first\nsecond");
        await Processor().ProcessAsync(admission, _telegram, CancellationToken.None);
        _store.Document!.Text.ShouldBe("synthetic first\nsecond");
        _store.Document.TextStatus.ShouldBe("read");
        _store.Finishes.ShouldHaveSingleItem().Reason.ShouldBeNull();
        _telegram.Reactions.ShouldBe(new[] { (-100L, 33, (string?)"✍") });
        _telegram.Sent.ShouldBeEmpty();
        _store.Attempts.ShouldBe(1);
        _store.Releases.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("synthetic.docx", null, "unsupported_format")]
    [InlineData("synthetic.txt", 20000001L, "too_large")]
    public async Task Unsupported_or_known_oversize_keeps_metadata_without_a_download(string name, long? size, string reason)
    {
        var admission = Admission(name, size);
        await Processor().ProcessAsync(admission, _telegram, CancellationToken.None);
        _store.Document!.TextStatus.ShouldBe("metadata_only");
        _store.Document.FailureReason.ShouldBe(reason);
        _store.Document.Text.ShouldBeNull();
        _telegram.DownloadedFiles.ShouldBeEmpty();
        _store.Attempts.ShouldBe(0);
        _telegram.Reactions.ShouldHaveSingleItem().Emoji.ShouldBe("✍");
        var sent = _telegram.Sent.ShouldHaveSingleItem();
        sent.TopicId.ShouldBe(7);
        sent.ReplyToMessageId.ShouldBe(33);
        sent.Text.ShouldContain(reason == "unsupported_format" ? "фото и сканы будут позже" : "20 МБ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task Actual_size_cap_wins_over_absent_or_underreported_size(long? reportedSize)
    {
        var admission = Admission(size: reportedSize);
        _telegram.Files[admission.Attachment.FileId] = new byte[20000001];
        await Processor().ProcessAsync(admission, _telegram, CancellationToken.None);
        _store.Finishes.ShouldHaveSingleItem().Reason.ShouldBe("too_large");
        _store.Document!.Text.ShouldBeNull();
        _telegram.DownloadedFiles.Count.ShouldBe(1);
        _store.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Transient_attempts_use_one_then_five_minute_delays_and_stop_at_three()
    {
        var admission = Admission();
        var processor = Processor();
        await processor.ProcessAsync(admission, _telegram, CancellationToken.None);
        _store.Finishes[0].ShouldBe(("processing", "unavailable", (DateTimeOffset?)Now.AddMinutes(1), (string?)null));
        _clock.UtcNow = Now.AddMinutes(1);
        await processor.ProcessAsync(_store.Admission!, _telegram, CancellationToken.None);
        _store.Finishes[1].ShouldBe(("processing", "unavailable", (DateTimeOffset?)Now.AddMinutes(6), (string?)null));
        _clock.UtcNow = Now.AddMinutes(6);
        await processor.ProcessAsync(_store.Admission!, _telegram, CancellationToken.None);
        _store.Finishes[2].ShouldBe(("failed", "unavailable", (DateTimeOffset?)null, (string?)null));
        await processor.ProcessAsync(_store.Admission!, _telegram, CancellationToken.None);
        _store.Attempts.ShouldBe(3);
        _telegram.DownloadedFiles.Count.ShouldBe(3);
        _telegram.Reactions.Count.ShouldBe(1);
        _telegram.Sent.Count.ShouldBe(1); // Fixed delivery attempts are persisted, not replayed.
    }

    [Fact]
    public async Task Interrupted_third_attempt_is_not_redownloaded()
    {
        await Processor().ProcessAsync(Admission(attempts: 3), _telegram, CancellationToken.None);
        _telegram.DownloadedFiles.ShouldBeEmpty();
        _store.Document!.TextStatus.ShouldBe("failed");
        _store.Document.FailureReason.ShouldBe("interrupted");
    }

    [Fact]
    public async Task Revoked_grant_pauses_without_attempt_reaction_or_notice()
    {
        _store.Authorized = false;
        await Processor().ProcessAsync(Admission(), _telegram, CancellationToken.None);
        _store.Pauses.ShouldBe(1);
        _store.Document.ShouldBeNull();
        _store.Attempts.ShouldBe(0);
        _telegram.DownloadedFiles.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_unknown_notice_and_reaction_are_never_automatically_sent_again()
    {
        var admission = Admission("synthetic.docx");
        _telegram.ThrowOnSend = true;
        _telegram.FailReactionTimes = 2;
        var processor = Processor();
        await processor.ProcessAsync(admission, _telegram, CancellationToken.None);
        _store.Admission!.ReactionAttempted.ShouldBeTrue();
        _store.Admission.NoticeAttempted.ShouldBeTrue();
        _telegram.ThrowOnSend = false;
        await processor.ProcessAsync(_store.Admission, _telegram, CancellationToken.None);
        _telegram.Sent.ShouldBeEmpty();
        _telegram.Reactions.ShouldBeEmpty();
        _telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parser_completion_precedes_gate_release_after_shutdown_or_lease_loss(bool leaseLoss)
    {
        var admission = Admission();
        _telegram.Files[admission.Attachment.FileId] = Encoding.UTF8.GetBytes("synthetic");
        var extractor = new HeldExtractor();
        var processor = Processor(extractor);
        using var cancellation = new CancellationTokenSource();
        var first = processor.ProcessAsync(admission, _telegram, cancellation.Token);
        await extractor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = processor.ProcessAsync(admission, _telegram, CancellationToken.None);
        if (leaseLoss) _leases.Current!.Cancel(); else await cancellation.CancelAsync();
        try
        {
            _store.Releases.ShouldBeEmpty();
            second.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            extractor.Release.TrySetResult();
        }
        if (leaseLoss) await first.WaitAsync(TimeSpan.FromSeconds(5));
        else await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        extractor.Calls.ShouldBe(2);
        _store.Finishes.Count.ShouldBe(1); // Only the later owner can commit.
        _store.Document!.Text.ShouldBe("synthetic completed");
        _store.Releases.Count.ShouldBe(2);
        _telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_is_throttled_and_unbound_orphans_never_download()
    {
        var admission = Admission() with { SourceMessageId = null };
        _store.Admission = admission;
        _store.Due = Enumerable.Range(1, 12).Select(_ => admission).ToArray();
        var processor = Processor();
        await processor.ResumeAsync(Scope, _telegram, CancellationToken.None);
        await processor.ResumeAsync(Scope, _telegram, CancellationToken.None);
        _store.DueReads.ShouldBe(1);
        _store.Deferrals.ShouldBe(10);
        _store.Bindings.ShouldBe(10);
        _telegram.DownloadedFiles.ShouldBeEmpty();
        _telegram.Sent.ShouldBeEmpty();
        _clock.UtcNow = Now.AddSeconds(60);
        await processor.ResumeAsync(Scope, _telegram, CancellationToken.None);
        _store.DueReads.ShouldBe(2);
        _store.Deferrals.ShouldBe(20);
    }

    [Fact]
    public async Task Retained_readable_source_is_not_downloaded_after_restart()
    {
        var admission = Admission();
        _store.Document = new(1, 22, Now, "synthetic.txt", null, "read", null, false, "completed", null, "already retained");
        await Processor().ProcessAsync(admission, _telegram, CancellationToken.None);
        _telegram.DownloadedFiles.ShouldBeEmpty();
        _store.Document.Text.ShouldBe("already retained");
        _store.Attempts.ShouldBe(0);
        _telegram.Sent.ShouldBeEmpty();
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class HeldExtractor : IDocumentTextExtractor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public DocumentTextExtraction Extract(Stream bytes, DocumentAttachment metadata, CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            return new("synthetic completed", null, false, false);
        }
    }
    private sealed class ManualLeaseKeeper : IHealthDocumentLeaseKeeper
    {
        public ManualOwner? Current { get; private set; }
        public Task<IHealthDocumentLeaseOwner> StartAsync(HealthDocumentLease lease, CancellationToken token)
        {
            Current = new(token);
            return Task.FromResult<IHealthDocumentLeaseOwner>(Current);
        }
    }
    private sealed class ManualOwner(CancellationToken token) : IHealthDocumentLeaseOwner
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(token);
        public CancellationToken Token => _source.Token;
        public void Cancel() => _source.Cancel();
        public ValueTask DisposeAsync() { _source.Dispose(); return ValueTask.CompletedTask; }
    }
}
