using System.Text;
using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Health.Documents;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Application.Health;

public sealed class HealthDocumentAssistantTests
{
    private static readonly DateTimeOffset Now = new(2030, 4, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReceivingBot Bot = new(10, 999, "test_health_bot", 42, "health");
    private readonly FakeHealthProfileStore _profiles = new();
    private readonly FakeFamilyOwnership _ownership = new();
    private readonly FakeEventStore _events = new();
    private readonly FakePendingRecordStore _pending = new();
    private readonly FakeMessageStore _messages = new();
    private readonly FakeLlmGateway _gateway = new();
    private readonly FakeTelegramClient _telegram = new();
    private readonly FakeHealthDocumentStore _documents = new();
    private readonly FixedClock _clock = new(Now);

    private HealthAssistant Assistant(ILlmGateway? gateway = null, bool llmOff = false)
    {
        var processor = new HealthDocumentProcessor(_documents, new DocumentTextExtractor(), new PassiveKeeper(),
            new HealthDocumentExecutionGate(_clock), _clock, NullLogger<HealthDocumentProcessor>.Instance);
        return new(_profiles, _ownership, _events, new FakeSafetyAlertStore(), _pending, _messages, gateway ?? _gateway,
            llmOff ? null : new LlmConfig { Budget = null, Models = [new("test", "synthetic")], FastModels = [], CallsPerMinute = 10,
                CallsPerDay = 100, MaxContextMessages = 10, MaxInputChars = 30000, MaxOutputTokens = 1000,
                CallTimeoutSeconds = 60, MaxConcurrentCalls = 1, ModelCooldownMinutes = 5, Prices = new Dictionary<string, ModelPrice>() },
            new FakeRolePrompts(), new FailureNoticeThrottle(), _clock, new BuildInfo("abcdef1", null, Now),
            NullLogger<HealthAssistant>.Instance, documents: _documents, documentProcessor: processor);
    }
    private static IncomingMessage Message(string? caption = null, string name = "synthetic.txt") =>
        new(-100, "supergroup", "synthetic group", 7, 33, 111, "synthetic-author", caption, MessageKind.Document,
            false, Now, null, null, "{}", null, null, new("synthetic-file", null, name, null, null));
    private async Task PostAsync(HealthAssistant assistant, IncomingMessage message, bool replyToAll = false)
    {
        await assistant.AdmitDocumentAsync(Bot, message, 100, CancellationToken.None);
        await assistant.HandleAsync(Bot, _telegram, message, new(StoreOutcome.Stored, 22), CancellationToken.None, replyToAll);
    }

    [Fact]
    public async Task Llm_off_upload_retains_text_without_any_model_or_diary_call()
    {
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("body glucose 19.0 /setprofile состояние changed");
        await PostAsync(Assistant(llmOff: true), Message());
        _documents.Document!.Text.ShouldBe("body glucose 19.0 /setprofile состояние changed");
        _gateway.Requests.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
        _pending.Added.ShouldBeEmpty();
        _profiles.Profile.Conditions.ShouldBeNull();
        _telegram.Reactions.ShouldHaveSingleItem().Emoji.ShouldBe("✍");
    }

    [Fact]
    public async Task File_failure_preserves_caption_record_and_deterministic_alert_without_duplicate_reaction()
    {
        _gateway.NextResult = LlmResult.Answered("""{"events":[{"type":"glucose","intent":"record","value":2.5}],"unclear":[],"needs_reply":false}""", "synthetic");
        await PostAsync(Assistant(), Message("сахар 2.5", "synthetic.docx"));
        _documents.Document!.TextStatus.ShouldBe("metadata_only");
        _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().PayloadJson.ShouldContain("2.5");
        _telegram.Sent.ShouldContain(s => s.Text.Contains("фото и сканы будут позже"));
        _telegram.Sent.ShouldContain(s => s.Text.Contains("🚨"));
        _telegram.Reactions.Count.ShouldBe(1);
        _gateway.Requests.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem().Text.ShouldBe("сахар 2.5");
    }

    [Fact]
    public async Task Eligible_caption_consults_committed_text_after_safety_and_body_never_enters_interpretation()
    {
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("body-only-sentinel glucose 19.0");
        _gateway.Results.Enqueue(LlmResult.Answered("""{"events":[{"type":"glucose","intent":"record","value":2.5}],"unclear":[],"needs_reply":true}""", "synthetic"));
        _gateway.Results.Enqueue(LlmResult.Answered("synthetic consultation", "synthetic"));
        await PostAsync(Assistant(), Message("сахар 2.5, поясните документ"), replyToAll: true);
        _gateway.Requests.Count.ShouldBe(2);
        _gateway.Requests[0].Messages.ShouldHaveSingleItem().Text.ShouldBe("сахар 2.5, поясните документ");
        _gateway.Requests[0].SystemPrompt.ShouldNotContain("body-only-sentinel");
        _gateway.Requests[1].SystemPrompt.ShouldContain("body-only-sentinel");
        _events.Added.ShouldHaveSingleItem().Events.Count.ShouldBe(1);
        var alert = _telegram.Sent.FindIndex(s => s.Text.Contains("🚨"));
        var answer = _telegram.Sent.FindIndex(s => s.Text.Contains("synthetic consultation"));
        alert.ShouldBeGreaterThanOrEqualTo(0);
        answer.ShouldBeGreaterThan(alert);
        _messages.OutgoingMessages.ShouldHaveSingleItem().Text.ShouldContain("synthetic consultation");
    }

    [Theory]
    [InlineData("/setprofile состояние changed")]
    [InlineData("удали это")]
    public async Task Caption_commands_and_model_undo_cannot_change_profile_or_delete_existing_events(string caption)
    {
        _ownership.OwnerUserIds.Add(111);
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("synthetic document");
        _gateway.NextResult = LlmResult.Answered("""{"events":[],"unclear":[],"undo":true,"needs_reply":false}""", "synthetic");
        var message = Message(caption) with { ReplyToMessageId = 20, ReplyToUserId = 999 };
        await PostAsync(Assistant(), message);
        _profiles.Profile.Conditions.ShouldBeNull();
        _events.DeleteCalls.ShouldBeEmpty();
        _pending.Added.ShouldBeEmpty();
        _documents.Document!.Text.ShouldBe("synthetic document");
    }

    [Theory]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Duplicate_upload_resumes_file_without_replaying_caption_model_or_pending(StoreOutcome outcome)
    {
        var assistant = Assistant();
        var message = Message("сахар 5.6");
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("synthetic document");
        await assistant.AdmitDocumentAsync(Bot, message, 100, CancellationToken.None);
        await assistant.HandleAsync(Bot, _telegram, message, new(outcome, 22), CancellationToken.None);
        _documents.Document!.Text.ShouldBe("synthetic document");
        await assistant.HandleAsync(Bot, _telegram, message, new(outcome, 22), CancellationToken.None);
        _telegram.DownloadedFiles.Count.ShouldBe(1);
        _telegram.Reactions.Count.ShouldBe(1);
        _gateway.Requests.ShouldBeEmpty();
        _events.Added.ShouldBeEmpty();
        _pending.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edited_document_does_not_replace_text_caption_or_events()
    {
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("original-body");
        var assistant = Assistant();
        var message = Message();
        await PostAsync(assistant, message);
        await assistant.HandleAsync(Bot, _telegram, message with { IsEdit = true, Text = "сахар 5.6", Document = new("replacement", null, "replacement.txt", null, null) },
            new(StoreOutcome.Updated, 22), CancellationToken.None);
        _documents.Document!.Text.ShouldBe("original-body");
        _telegram.DownloadedFiles.Count.ShouldBe(1);
        _gateway.Requests.ShouldBeEmpty();
        _events.Replaced.ShouldBeEmpty();
    }

    [Fact]
    public async Task Metadata_failure_gets_no_success_reaction_but_caption_still_records()
    {
        _documents.ClaimFailure = new IOException("synthetic-private-sentinel");
        _gateway.NextResult = LlmResult.Answered("""{"events":[{"type":"glucose","intent":"record","value":5.6}],"unclear":[],"needs_reply":false}""", "synthetic");
        await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(Assistant(), Message("сахар 5.6")));
        _documents.Document.ShouldBeNull();
        _events.Added.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().Type.ShouldBe("glucose");
        _telegram.Reactions.Count.ShouldBe(1); // Caption record alone.
        _telegram.Sent.ShouldContain(s => s.Text == HealthDocumentProcessor.CannotSave);
        _telegram.Sent.ShouldAllBe(s => !s.Text.Contains("synthetic-private-sentinel"));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("unsure")]
    public async Task Delete_while_caption_model_is_held_cannot_recreate_events_pending_or_reaction(string intent)
    {
        _telegram.Files["synthetic-file"] = Encoding.UTF8.GetBytes("synthetic body");
        _gateway.NextResult = LlmResult.Answered("{\"events\":[{\"type\":\"glucose\",\"intent\":\"" + intent + "\",\"value\":5.6}],\"unclear\":[],\"needs_reply\":false}", "synthetic");
        var held = new HeldGateway(_gateway);
        _documents.Deletion = new(true, new([], [new(-100, 33)]), []);
        _documents.OnDelete = () =>
        {
            _events.ThrowOnAdd = new InvalidOperationException("Document source is no longer active.");
            _pending.ThrowOnAdd = new InvalidOperationException("Document source is no longer active.");
        };
        var assistant = Assistant(held);
        var handling = PostAsync(assistant, Message("сахар 5.6"));
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await assistant.HandleAsync(Bot, _telegram, Message("/del") with { Kind = MessageKind.Text, Document = null,
                MessageId = 34, ReplyToMessageId = 33 }, new(StoreOutcome.Stored, 23), CancellationToken.None);
        }
        finally { held.Release.TrySetResult(); }
        await Should.ThrowAsync<InvalidOperationException>(() => handling.WaitAsync(TimeSpan.FromSeconds(5)));
        _events.Added.ShouldBeEmpty();
        _pending.Added.ShouldBeEmpty();
        _telegram.ButtonMessages.ShouldBeEmpty();
        _telegram.Reactions.ShouldBe(new[] { (-100L, 33, (string?)"✍"), (-100L, 33, (string?)null) });
    }

    [Fact]
    public async Task Docs_lists_profile_library_without_model_or_download_and_splits_long_output()
    {
        _documents.Inventory = Enumerable.Range(1, 10).Select(i => new HealthDocumentInfo(i, i + 100, Now.AddDays(-i),
            "synthetic-" + i + ".txt", "caption-marker-" + i + new string('c', 490), "metadata_only", "unsupported_format", false, "completed", null)).ToArray();
        await Assistant().HandleAsync(Bot, _telegram, Message("/docs") with { Kind = MessageKind.Text, Document = null },
            new(StoreOutcome.Stored, 22), CancellationToken.None);
        _telegram.Sent.Count.ShouldBeGreaterThan(1);
        _telegram.Sent.ShouldAllBe(p => p.TopicId == 7 && p.ReplyToMessageId == 33 && p.Text.Length <= 4096);
        var text = string.Concat(_telegram.Sent.Select(p => p.Text));
        foreach (var i in Enumerable.Range(1, 10)) text.ShouldContain("caption-marker-" + i);
        _gateway.Requests.ShouldBeEmpty();
        _telegram.DownloadedFiles.ShouldBeEmpty();
    }

    private sealed class PassiveKeeper : IHealthDocumentLeaseKeeper
    {
        public Task<IHealthDocumentLeaseOwner> StartAsync(HealthDocumentLease lease, CancellationToken token) =>
            Task.FromResult<IHealthDocumentLeaseOwner>(new Owner(token));
        private sealed class Owner(CancellationToken token) : IHealthDocumentLeaseOwner
        { public CancellationToken Token => token; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class HeldGateway(FakeLlmGateway inner) : ILlmGateway
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled => inner.IsEnabled;
        public IReadOnlyList<ModelStatus> DescribeModels() => inner.DescribeModels();
        public bool IsKnownModel(string name) => inner.IsKnownModel(name);
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await inner.CompleteAsync(request, token);
        }
    }
}
