using System.Text.Json;
using Assistant.Application.Llm;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet.Photos;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging;

namespace Assistant.UnitTests.Application.Vet.Photos;

public sealed class VetPhotoProcessorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly VetDiaryScope Scope = new(1, 2, 1001, -100, 7);
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InputId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AttemptKey = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ClaimToken = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly byte[] Bytes = [137, 80, 78, 71, 13, 10, 26, 10, 42, 43];
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2031-05-12T12:00:00Z");
    private static VetPhotoWork Work => new(Scope, SourceId, InputId, 222);

    [Fact]
    public async Task Marker_precedes_one_minimal_request_with_exact_bytes_database_trigger_and_durable_identity()
    {
        var f = new Fixture(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Gateway.WaitBeforeAnswering = release.Task;
        var operation = f.Processor().ProcessAsync(Work, f.Telegram, Ct);
        try
        {
            f.Ports.Order.ShouldBe(["source", "stored", "reserve", "claim", "input", "original", "dispatch"]);
            var request = f.Gateway.Requests.Single();
            request.FamilyId.ShouldBe(1); request.BotId.ShouldBe(1001); request.ChatId.ShouldBe(-100);
            request.TopicId.ShouldBe(7);
            request.TriggerMessageId.ShouldBe(901);
            request.AttemptKey.ShouldBe(AttemptKey);
            request.PreferredModel.ShouldBe("gpt-6.1-sol"); request.Tier.ShouldBe("fast"); request.SystemPrompt.ShouldBe(f.Prompts.Text);
            var message = request.Messages.Single(); message.Role.ShouldBe(LlmMessageRole.User); message.Author.ShouldBeNull();
            using var input = JsonDocument.Parse(message.Text);
            input.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[] { "input_revision_id", "photo_source_id", "untrusted_caption" });
            input.RootElement.GetProperty("photo_source_id").GetGuid().ShouldBe(SourceId);
            input.RootElement.GetProperty("input_revision_id").GetGuid().ShouldBe(InputId);
            input.RootElement.GetProperty("untrusted_caption").GetString().ShouldBe("synthetic caption");
            var image = request.Images.ShouldNotBeNull().Single(); image.MediaType.ShouldBe("image/png"); image.Data.ToArray().ShouldBe(Bytes);
        }
        finally { release.TrySetResult(); await operation; }
        (await operation).Status.ShouldBe(VetPhotoProcessStatus.Review);
        f.Gateway.Requests.Count.ShouldBe(1); var completed = f.Ports.Completions.Single();
        completed.StructuredJson.ShouldBe("synthetic response"); completed.DiagnosticAttemptId.ShouldBe(f.TraceId);
        completed.ActorUserId.ShouldBe(222); completed.ModelName.ShouldBe("gpt-6.1-sol");
        f.Ports.ReadBuffer!.ShouldAllBe(b => b == 0); f.Ports.Releases.ShouldBe(1); f.Telegram.Downloads.ShouldBe(0);
        f.Time.Deadlines.ShouldBe([TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5)]);
    }

    [Fact]
    public async Task Historical_selection_sends_immutable_old_caption_and_returns_protected_delta_without_pointer_change()
    {
        var f = new Fixture(); var current = Guid.NewGuid();
        f.Ports.Admission.Source!.CurrentInputRevisionId = current;
        f.Ports.Admission = f.Ports.Admission with { Input = f.Ports.Input(current, "synthetic new caption") };
        f.Ports.SelectedInput = f.Ports.Input(InputId, "synthetic old caption");
        f.Ports.Claim = f.Ports.Claim with { ExpectedCurrentInputId = current, HistoricalSelection = true, RunWindowId = Guid.NewGuid() };
        var delta = new VetPhotoCandidateDelta(Guid.NewGuid(), 9, SourceId, InputId, f.Ports.Extraction.Id, true);
        f.Ports.Completed = new(VetPhotoImageStatus.ProposedDelta, Extraction: f.Ports.Extraction, Delta: delta);
        var result = await f.Processor().ProcessAsync(Work with { ScheduledAttemptKey = AttemptKey }, f.Telegram, Ct);
        result.Status.ShouldBe(VetPhotoProcessStatus.Review); result.Delta.ShouldBe(delta);
        f.Ports.ScheduledKeys.ShouldBe([AttemptKey]); f.Ports.CurrentClaims.ShouldBe(0); f.Ports.Reservations.ShouldBe(0);
        using var input = JsonDocument.Parse(f.Gateway.Requests.Single().Messages.Single().Text);
        input.RootElement.GetProperty("untrusted_caption").GetString().ShouldBe("synthetic old caption");
        input.RootElement.GetProperty("input_revision_id").GetGuid().ShouldBe(InputId);
        f.Ports.Admission.Source.CurrentInputRevisionId.ShouldBe(current);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stored_success_replays_before_disabled_provider_or_deleted_original_checks(bool scheduled)
    {
        var f = new Fixture(); f.Gateway.IsEnabled = false; f.Ports.Stored = new(VetPhotoImageStatus.Existing, Extraction: f.Ports.Extraction);
        f.Ports.Reservation = new(VetPhotoArchiveStatus.OriginalDeleted, null, null);
        var result = await f.Processor().ProcessAsync(Work with { ScheduledAttemptKey = scheduled ? AttemptKey : null }, f.Telegram, Ct);
        result.Status.ShouldBe(VetPhotoProcessStatus.Review); result.Extraction!.Id.ShouldBe(f.Ports.Extraction.Id);
        f.Ports.Order.ShouldBe(["source", "stored"]); f.Ports.Reservations.ShouldBe(0); f.Gateway.Requests.ShouldBeEmpty(); f.Telegram.Downloads.ShouldBe(0);
    }

    [Theory]
    [InlineData("subscription")] [InlineData("disabled")] [InlineData("model")] [InlineData("missing_model")]
    [InlineData("unavailable")] [InlineData("text_only")] [InlineData("prompt")]
    public async Task Unusable_image_configuration_has_zero_claims_and_calls(string reason)
    {
        var f = new Fixture(); var options = new VetPhotoRuntimeOptions(reason != "subscription", reason == "model" ? "other-model" : "gpt-6.1-sol");
        if (reason == "disabled") f.Gateway.IsEnabled = false;
        if (reason == "missing_model") f.Gateway.Models.Clear();
        if (reason == "unavailable") f.Gateway.Models = [new("gpt-6.1-sol", false, null, true)];
        if (reason == "text_only") f.Gateway.Models = [new("gpt-6.1-sol", true, null, false)];
        if (reason == "prompt") f.Prompts.Text = null;
        (await f.Processor(options).ProcessAsync(Work, f.Telegram, Ct)).ShouldBe(new(VetPhotoProcessStatus.Failed, "image_provider_unavailable"));
        f.Ports.CurrentClaims.ShouldBe(0); f.Gateway.Requests.ShouldBeEmpty(); f.Ports.Completions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(VetPhotoImageStatus.Busy, VetPhotoProcessStatus.Deferred, "busy_or_stale")]
    [InlineData(VetPhotoImageStatus.Stale, VetPhotoProcessStatus.Deferred, "busy_or_stale")]
    [InlineData(VetPhotoImageStatus.Refused, VetPhotoProcessStatus.Deferred, "busy_or_stale")]
    [InlineData(VetPhotoImageStatus.CapacityFull, VetPhotoProcessStatus.Deferred, "archive_full")]
    [InlineData(VetPhotoImageStatus.Unknown, VetPhotoProcessStatus.Unknown, "outcome_unknown")]
    public async Task Claim_refusal_never_becomes_success_or_a_provider_retry(VetPhotoImageStatus status, VetPhotoProcessStatus expected, string category)
    {
        var f = new Fixture(); f.Ports.ClaimResult = new(status);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).ShouldBe(new(expected, category));
        f.Gateway.Requests.ShouldBeEmpty(); f.Ports.Completions.ShouldBeEmpty(); f.Ports.Reads.ShouldBe(0); f.Ports.Failures.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(VetPhotoImageStatus.Stale)] [InlineData(VetPhotoImageStatus.Refused)] [InlineData(VetPhotoImageStatus.Busy)] [InlineData(VetPhotoImageStatus.Existing)]
    public async Task Completion_without_durable_evidence_is_deferred(VetPhotoImageStatus status)
    {
        var f = new Fixture(); f.Ports.Completed = new(status);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).ShouldBe(new(VetPhotoProcessStatus.Deferred, "busy_or_stale"));
        f.Gateway.Requests.Count.ShouldBe(1); f.Ports.Completions.Count.ShouldBe(1); f.Ports.Releases.ShouldBe(1);
    }

    [Theory]
    [InlineData("source")] [InlineData("input")] [InlineData("scope")] [InlineData("actor")] [InlineData("scheduled_key")]
    public async Task Claim_echo_mismatch_has_zero_dispatches(string mismatch)
    {
        var f = new Fixture(); f.Ports.Claim = mismatch switch
        {
            "source" => f.Ports.Claim with { SourceId = Guid.NewGuid() }, "input" => f.Ports.Claim with { InputRevisionId = Guid.NewGuid() },
            "scope" => f.Ports.Claim with { Scope = Scope with { TopicId = 8 } }, "actor" => f.Ports.Claim with { ActorUserId = 111 },
            _ => f.Ports.Claim with { AttemptKey = Guid.NewGuid() }
        };
        (await f.Processor().ProcessAsync(mismatch == "scheduled_key" ? Work with { ScheduledAttemptKey = AttemptKey } : Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Deferred);
        f.Gateway.Requests.ShouldBeEmpty(); f.Ports.Reads.ShouldBe(0); f.Ports.Dispatches.ShouldBe(0);
    }

    [Theory]
    [InlineData("input")] [InlineData("original")] [InlineData("marker")]
    public async Task Lost_pre_dispatch_proof_releases_only_unstarted_accounting(string loss)
    {
        var f = new Fixture(); if (loss == "input") f.Ports.SelectedInput = null;
        if (loss == "original") f.Ports.NoOriginal = true; if (loss == "marker") f.Ports.CanDispatch = false;
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(loss == "original" ? VetPhotoProcessStatus.Failed : VetPhotoProcessStatus.Deferred);
        f.Ports.Failures.Single().ShouldBe(("provider_unavailable", VetPhotoImageFailureDisposition.KnownNotDispatched));
        f.Gateway.Requests.ShouldBeEmpty(); f.Ports.Completions.ShouldBeEmpty(); f.Ports.Releases.ShouldBe(loss == "marker" ? 1 : 0);
    }

    [Theory]
    [InlineData(LlmRefusalReason.NotConfigured)] [InlineData(LlmRefusalReason.RateLimited)]
    [InlineData(LlmRefusalReason.UnsupportedInput)] [InlineData(LlmRefusalReason.OutcomeUnknown)]
    public async Task Refusal_records_known_or_unknown_accounting_without_second_call(LlmRefusalReason reason)
    {
        var f = new Fixture(); f.Gateway.NextResult = LlmResult.Refused(reason); var unknown = reason == LlmRefusalReason.OutcomeUnknown;
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(unknown ? VetPhotoProcessStatus.Unknown : VetPhotoProcessStatus.Failed);
        f.Ports.Failures.Single().Disposition.ShouldBe(unknown ? VetPhotoImageFailureDisposition.OutcomeUnknown : VetPhotoImageFailureDisposition.KnownNotDispatched);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(unknown ? VetPhotoProcessStatus.Unknown : VetPhotoProcessStatus.Deferred);
        f.Gateway.Requests.Count.ShouldBe(1); f.Ports.Completions.ShouldBeEmpty(); f.Ports.Releases.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lost_response_or_commit_is_unknown_and_logs_only_exception_type(bool commit)
    {
        var f = new Fixture(); const string sentinel = "synthetic-private-caption-and-opaque-url";
        if (commit) f.Ports.CompletionException = new IOException(sentinel); else f.Gateway.ThrowOnComplete = new IOException(sentinel);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Unknown);
        f.Ports.Failures.Single().Disposition.ShouldBe(VetPhotoImageFailureDisposition.OutcomeUnknown);
        f.Log.Lines.ShouldContain(line => line.Contains("IOException", StringComparison.Ordinal)); f.Log.Lines.ShouldAllBe(line => !line.Contains(sentinel, StringComparison.Ordinal));
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Unknown);
        f.Gateway.Requests.Count.ShouldBe(1); f.Ports.ReadBuffer!.ShouldAllBe(b => b == 0); f.Ports.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task Image_deadline_is_90_seconds_and_unknown_cleanup_has_independent_5_second_tokens()
    {
        var f = new Fixture(); f.Gateway.WaitBeforeAnswering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var operation = f.Processor().ProcessAsync(Work, f.Telegram, Ct); f.Time.Fire(TimeSpan.FromSeconds(90));
        (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Status.ShouldBe(VetPhotoProcessStatus.Unknown);
        f.Ports.Failures.Single().Disposition.ShouldBe(VetPhotoImageFailureDisposition.OutcomeUnknown);
        f.Ports.FailureTokenCancelled.ShouldBeFalse(); f.Ports.ReleaseTokenCancelled.ShouldBeFalse();
        f.Time.Deadlines.ShouldBe([TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)]);
        f.Ports.ReadBuffer!.ShouldAllBe(b => b == 0); f.Gateway.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Caller_cancellation_rethrows_and_cleans_before_or_after_dispatch(bool afterDispatch)
    {
        var f = new Fixture(); using var cancelled = new CancellationTokenSource();
        if (afterDispatch) f.Gateway.WaitBeforeAnswering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        else f.Ports.BeforeRead = () => cancelled.Cancel();
        var operation = f.Processor().ProcessAsync(Work, f.Telegram, cancelled.Token); if (afterDispatch) cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => operation);
        f.Ports.Failures.Single().Disposition.ShouldBe(afterDispatch ? VetPhotoImageFailureDisposition.OutcomeUnknown : VetPhotoImageFailureDisposition.KnownNotDispatched);
        f.Ports.FailureTokenCancelled.ShouldBeFalse(); f.Gateway.Requests.Count.ShouldBe(afterDispatch ? 1 : 0); f.Ports.Releases.ShouldBe(afterDispatch ? 1 : 0);
    }

    [Fact]
    public async Task Download_commits_exact_bytes_before_claim_and_clears_transport_and_model_copies()
    {
        var f = new Fixture(); f.Ports.Reservation = f.Ports.Download();
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Review);
        f.Telegram.Downloads.ShouldBe(1); f.Telegram.FileId.ShouldBe("synthetic-file"); f.Telegram.MaxBytes.ShouldBe(10_485_760);
        f.Ports.CommittedCopy.ShouldBe(Bytes); f.Ports.Commit.ActorUserId.ShouldBe(222); f.Ports.Commit.InputRevisionId.ShouldBe(InputId);
        f.Telegram.Buffer!.ShouldAllBe(b => b == 0); f.Ports.Commit.Original.ToArray().ShouldAllBe(b => b == 0);
        f.Ports.Order.IndexOf("archive_commit").ShouldBeLessThan(f.Ports.Order.IndexOf("claim"));
        f.Time.Deadlines.ShouldBe([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5)]);
    }

    [Theory]
    [InlineData(TelegramFileDownloadFailure.Timeout, "download_timeout", true)]
    [InlineData(TelegramFileDownloadFailure.TooLarge, "download_too_large", false)]
    [InlineData(TelegramFileDownloadFailure.Unavailable, "download_unavailable", false)]
    public async Task Download_categories_only_retry_known_timeout(TelegramFileDownloadFailure reason, string category, bool transient)
    {
        var f = new Fixture(); f.Ports.Reservation = f.Ports.Download(); f.Telegram.Hook = (_, _) => throw new TelegramFileDownloadException(reason);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Category.ShouldBe(category);
        f.Ports.DownloadFailures.Single().ShouldBe((category, transient)); f.Gateway.Requests.ShouldBeEmpty(); f.Ports.CurrentClaims.ShouldBe(0); f.Ports.CommittedCopy.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Download_deadline_or_caller_cancellation_clears_partial_buffer_and_freezes_retry_disposition(bool caller)
    {
        var f = new Fixture(); f.Ports.Reservation = f.Ports.Download(); using var cancelled = new CancellationTokenSource();
        f.Telegram.Hook = async (stream, token) => { await stream.WriteAsync(Bytes, token); f.Telegram.Capture(stream);
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token); return 0; };
        var operation = f.Processor().ProcessAsync(Work, f.Telegram, cancelled.Token);
        if (caller) cancelled.Cancel(); else f.Time.Fire(TimeSpan.FromSeconds(30));
        if (caller) await Should.ThrowAsync<OperationCanceledException>(() => operation);
        else (await operation.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(new(VetPhotoProcessStatus.Deferred, "download_timeout"));
        f.Ports.DownloadFailures.Single().ShouldBe((caller ? "download_cancelled" : "download_timeout", !caller));
        f.Ports.DownloadFailureTokenCancelled.ShouldBeFalse(); f.Telegram.Buffer!.ShouldAllBe(b => b == 0); f.Gateway.Requests.ShouldBeEmpty();
        f.Time.Deadlines.ShouldBe(caller ? new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5) } : new[] { TimeSpan.FromSeconds(30) });
    }

    [Theory]
    [InlineData("decoder")] [InlineData("count")] [InlineData("capacity")]
    public async Task Invalid_download_or_archive_capacity_never_enters_image_claim(string invalid)
    {
        var f = new Fixture(); f.Ports.Reservation = f.Ports.Download();
        if (invalid == "decoder") f.Decoder.Result = new(null, "invalid_image");
        if (invalid == "count") f.Telegram.Hook = (_, _) => Task.FromResult(1L);
        if (invalid == "capacity") f.Ports.CommitResult = new(VetPhotoArchiveStatus.CapacityFull, null);
        var result = await f.Processor().ProcessAsync(Work, f.Telegram, Ct);
        result.Category.ShouldBe(invalid == "capacity" ? "archive_full" : invalid == "decoder" ? "invalid_image" : "download_unavailable");
        f.Ports.CurrentClaims.ShouldBe(0); f.Gateway.Requests.ShouldBeEmpty(); if (invalid != "capacity") f.Ports.DownloadFailures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Caption_reuse_is_zero_call_and_changed_full_prompt_changes_reuse_and_completion_version()
    {
        var f = new Fixture(); f.Ports.Admission.Input!.ReusesImageInputId = Guid.NewGuid(); f.Ports.ReuseResult = new(VetPhotoImageStatus.Reused, Extraction: f.Ports.Extraction);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Review);
        var old = f.Ports.ReuseVersions.Single(); old.Length.ShouldBe(30); old.ShouldStartWith("v1-"); f.Gateway.Requests.ShouldBeEmpty(); f.Ports.CurrentClaims.ShouldBe(0);
        f.Prompts.Text += "\nsynthetic changed instruction"; f.Ports.ReuseResult = new(VetPhotoImageStatus.NotFound);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Review);
        f.Ports.ReuseVersions.Last().ShouldNotBe(old); f.Ports.Completions.Single().PromptVersion.ShouldBe(f.Ports.ReuseVersions.Last()); f.Gateway.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("source")] [InlineData("input")] [InlineData("actor")]
    [InlineData("unbound")] [InlineData("stale")]
    public async Task Invalid_or_unbound_work_has_no_download_claim_or_provider_effect(string invalid)
    {
        var f = new Fixture(); var work = invalid switch
        {
            "source" => Work with { SourceId = Guid.Empty }, "input" => Work with { InputRevisionId = Guid.Empty },
            "actor" => Work with { ActorUserId = 0 }, "stale" => Work with { InputRevisionId = Guid.NewGuid() }, _ => Work
        };
        if (invalid == "unbound") f.Ports.Admission.Source!.SourceMessageDbId = null;
        var result = await f.Processor().ProcessAsync(work, f.Telegram, Ct);
        result.Status.ShouldBe(invalid is "source" or "input" or "actor" ? VetPhotoProcessStatus.Failed : VetPhotoProcessStatus.Deferred);
        f.Ports.Reservations.ShouldBe(0); f.Ports.CurrentClaims.ShouldBe(0); f.Telegram.Downloads.ShouldBe(0); f.Gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recovery_or_reader_cleanup_is_bounded_by_its_own_five_second_timer(bool reader)
    {
        var f = new Fixture(); var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (reader) f.Ports.ReleaseWait = ct => wait.Task.WaitAsync(ct);
        else { f.Gateway.ThrowOnComplete = new IOException("synthetic hidden failure"); f.Ports.FailureWait = ct => wait.Task.WaitAsync(ct); }
        var operation = f.Processor().ProcessAsync(Work, f.Telegram, Ct);
        operation.IsCompleted.ShouldBeFalse(); f.Time.Fire(TimeSpan.FromSeconds(5));
        (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Status.ShouldBe(reader ? VetPhotoProcessStatus.Review : VetPhotoProcessStatus.Unknown);
        f.Log.Lines.ShouldContain(line => line.Contains("OperationCanceledException", StringComparison.Ordinal) || line.Contains("TaskCanceledException", StringComparison.Ordinal));
        f.Ports.ReadBuffer!.ShouldAllBe(b => b == 0); f.Ports.Releases.ShouldBe(1); f.Gateway.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lost_marker_commit_response_is_unknown_even_before_any_provider_call()
    {
        var f = new Fixture(); f.Ports.DispatchException = new IOException("synthetic lost marker response");
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Unknown);
        f.Ports.Failures.Single().ShouldBe(("outcome_unknown", VetPhotoImageFailureDisposition.OutcomeUnknown));
        f.Gateway.Requests.ShouldBeEmpty(); f.Ports.Completions.ShouldBeEmpty(); f.Ports.Releases.ShouldBe(1);
        f.Ports.ReadBuffer!.ShouldAllBe(b => b == 0);
        (await f.Processor().ProcessAsync(Work, f.Telegram, Ct)).Status.ShouldBe(VetPhotoProcessStatus.Unknown);
        f.Ports.Dispatches.ShouldBe(1); f.Gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("retained")] [InlineData("download")] [InlineData("superseded")]
    public async Task Archive_only_work_commits_full_original_or_replays_retention_without_any_image_or_provider_path(string scenario)
    {
        var f = new Fixture(); f.Gateway.IsEnabled = false; f.Prompts.Text = null;
        f.Ports.Stored = new(VetPhotoImageStatus.Existing, Extraction: f.Ports.Extraction);
        var work = Work with { ArchiveOnly = true };
        if (scenario != "retained")
        {
            var reservation = f.Ports.Download();
            if (scenario == "superseded")
            {
                var current = Guid.NewGuid(); f.Ports.Admission.Source!.CurrentInputRevisionId = current;
                f.Ports.Admission = f.Ports.Admission with { Input = f.Ports.Input(current, "synthetic changed caption") };
                reservation = reservation with { Claim = reservation.Claim! with { Source = f.Ports.Admission.Source! } };
            }
            f.Ports.Reservation = reservation;
        }
        var result = await f.Processor(new(false, "other-model")).ProcessAsync(work, f.Telegram, Ct);
        result.ShouldBe(new(VetPhotoProcessStatus.Deferred, "archive_retained")); result.Extraction.ShouldBeNull(); result.Delta.ShouldBeNull();
        f.Ports.Reservations.ShouldBe(1); f.Ports.Order.ShouldBe(scenario == "retained"
            ? new[] { "source", "reserve" } : new[] { "source", "reserve", "archive_commit" });
        if (scenario != "retained")
        {
            f.Telegram.Downloads.ShouldBe(1); f.Ports.CommittedCopy.ShouldBe(Bytes);
            f.Ports.Commit.InputRevisionId.ShouldBe(InputId); f.Ports.Commit.ActorUserId.ShouldBe(222);
            f.Ports.Commit.Original.ToArray().ShouldAllBe(b => b == 0); f.Telegram.Buffer!.ShouldAllBe(b => b == 0);
            f.Time.Deadlines.ShouldBe([TimeSpan.FromSeconds(30)]);
        }
        else { f.Telegram.Downloads.ShouldBe(0); f.Time.Deadlines.ShouldBeEmpty(); }
        f.Ports.CurrentClaims.ShouldBe(0); f.Ports.ScheduledKeys.ShouldBeEmpty(); f.Ports.Dispatches.ShouldBe(0);
        f.Ports.Completions.ShouldBeEmpty(); f.Ports.Reads.ShouldBe(0); f.Ports.Releases.ShouldBe(0);
        f.Ports.ReuseVersions.ShouldBeEmpty(); f.Ports.Failures.ShouldBeEmpty(); f.Gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(VetPhotoArchiveStatus.CapacityFull, "archive_full")]
    [InlineData(VetPhotoArchiveStatus.OriginalDeleted, "reupload_required")]
    [InlineData(VetPhotoArchiveStatus.InvalidImage, "encoded_image_too_large")]
    [InlineData(VetPhotoArchiveStatus.Refused, "download_unavailable")]
    public async Task Archive_only_refusal_never_uses_a_cached_model_result_or_reacquires_deleted_Telegram_file(VetPhotoArchiveStatus status, string category)
    {
        var f = new Fixture(); f.Ports.Stored = new(VetPhotoImageStatus.Existing, Extraction: f.Ports.Extraction);
        f.Ports.Reservation = new(status, null, null);
        (await f.Processor().ProcessAsync(Work with { ArchiveOnly = true }, f.Telegram, Ct))
            .ShouldBe(new(VetPhotoProcessStatus.Failed, category));
        f.Ports.Order.ShouldBe(["source", "reserve"]); f.Telegram.Downloads.ShouldBe(0);
        f.Ports.CurrentClaims.ShouldBe(0); f.Ports.ScheduledKeys.ShouldBeEmpty(); f.Ports.Dispatches.ShouldBe(0);
        f.Ports.Completions.ShouldBeEmpty(); f.Ports.ReuseVersions.ShouldBeEmpty(); f.Gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Archive_only_and_scheduled_attempt_cannot_be_combined_to_bypass_the_accepted_run_path()
    {
        var f = new Fixture();
        (await f.Processor().ProcessAsync(Work with { ArchiveOnly = true, ScheduledAttemptKey = AttemptKey }, f.Telegram, Ct))
            .ShouldBe(new(VetPhotoProcessStatus.Deferred, "stale_input"));
        f.Ports.Order.ShouldBe(["source"]); f.Ports.Reservations.ShouldBe(0); f.Ports.CurrentClaims.ShouldBe(0);
        f.Ports.ScheduledKeys.ShouldBeEmpty(); f.Ports.Dispatches.ShouldBe(0); f.Gateway.Requests.ShouldBeEmpty(); f.Telegram.Downloads.ShouldBe(0);
    }

    [Theory]
    [InlineData(TelegramFileDownloadFailure.Timeout, "download_timeout", true)]
    [InlineData(TelegramFileDownloadFailure.TooLarge, "download_too_large", false)]
    [InlineData(TelegramFileDownloadFailure.Unavailable, "download_unavailable", false)]
    public async Task Archive_only_transport_failure_records_only_the_download_disposition_without_entering_vision(TelegramFileDownloadFailure failure, string category, bool transient)
    {
        var f = new Fixture(); f.Ports.Reservation = f.Ports.Download(); f.Telegram.Hook = (_, _) => Task.FromException<long>(new TelegramFileDownloadException(failure));
        (await f.Processor().ProcessAsync(Work with { ArchiveOnly = true }, f.Telegram, Ct)).Category.ShouldBe(category);
        f.Ports.DownloadFailures.Single().ShouldBe((category, transient)); f.Ports.CommittedCopy.ShouldBeNull();
        f.Ports.CurrentClaims.ShouldBe(0); f.Ports.ScheduledKeys.ShouldBeEmpty(); f.Ports.Dispatches.ShouldBe(0);
        f.Ports.Completions.ShouldBeEmpty(); f.Ports.ReuseVersions.ShouldBeEmpty(); f.Ports.Failures.ShouldBeEmpty(); f.Gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Archive_only_unbound_or_inconsistent_current_source_never_exposes_bytes_or_stored_result(bool inconsistent)
    {
        var f = new Fixture(); f.Ports.Stored = new(VetPhotoImageStatus.Existing, Extraction: f.Ports.Extraction);
        if (inconsistent) f.Ports.Admission.Source!.CurrentInputRevisionId = Guid.NewGuid();
        else f.Ports.Admission.Source!.SourceMessageDbId = null;
        (await f.Processor().ProcessAsync(Work with { ArchiveOnly = true }, f.Telegram, Ct))
            .ShouldBe(new(VetPhotoProcessStatus.Deferred, "unbound_source"));
        f.Ports.Order.ShouldBe(["source"]); f.Ports.Reservations.ShouldBe(0); f.Telegram.Downloads.ShouldBe(0);
        f.Ports.CurrentClaims.ShouldBe(0); f.Ports.Dispatches.ShouldBe(0); f.Ports.Completions.ShouldBeEmpty(); f.Gateway.Requests.ShouldBeEmpty();
    }
    private sealed class Fixture
    {
        public Ports Ports { get; } = new(); public DownloadTelegram Telegram { get; } = new();
        public Decoder Decoder { get; } = new(); public Prompts Prompts { get; } = new(); public CaptureLog Log { get; } = new();
        public ManualTime Time { get; } = new(); public Guid TraceId { get; } = Guid.NewGuid();
        public FakeLlmGateway Gateway { get; } = new() { Models = [new("gpt-6.1-sol", true, null, true)] };
        public Fixture() { Gateway.NextResult = LlmResult.Answered("synthetic response", "gpt-6.1-sol") with { TraceAttemptId = TraceId }; }
        public VetPhotoProcessor Processor(VetPhotoRuntimeOptions? options = null) =>
            new(Ports, Ports, Ports, Decoder, Gateway, Prompts, options ?? new(true), Log, Time);
    }
    private sealed class Prompts : IRolePrompts
    {
        public string? Text { get; set; } = "Synthetic full photo extraction role instruction.";
        public IReadOnlyList<string> Missing => [];
        public string? Find(string role, string fileName) => role == "vet" && fileName == "photo-extract.md" ? Text : null;
    }
    private sealed class Decoder : IVetPhotoImageDecoder
    {
        public VetPhotoImageDecodeResult Result { get; set; } = new(new(2, 2, "image/png", Bytes.Length, 16), null);
        public VetPhotoImageDecodeResult Decode(ReadOnlyMemory<byte> original, CancellationToken token)
        { token.ThrowIfCancellationRequested(); original.ToArray().ShouldBe(Bytes); return Result; }
    }
    private sealed class CaptureLog : ILogger<VetPhotoProcessor>
    {
        public List<string> Lines { get; } = []; public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { exception.ShouldBeNull(); Lines.Add(formatter(state, exception)); }
    }
    private sealed class ManualTime : TimeProvider
    {
        public List<ManualTimer> Created { get; } = [];
        public IReadOnlyList<TimeSpan> Deadlines => Created.Select(t => t.Due).ToArray();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { period.ShouldBe(Timeout.InfiniteTimeSpan); var timer = new ManualTimer(callback, state, dueTime); Created.Add(timer); return timer; }
        public void Fire(TimeSpan due) { foreach (var timer in Created.Where(t => t.Due == due).ToArray()) timer.Fire(); }
        public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; } = due; private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class DownloadTelegram : FakeTelegramClient, ITelegramClient
    {
        public int Downloads { get; private set; } public string? FileId { get; private set; }
        public long MaxBytes { get; private set; } public byte[]? Buffer { get; private set; }
        public Func<Stream, CancellationToken, Task<long>>? Hook { get; set; }
        public void Capture(Stream stream) { if (((MemoryStream)stream).TryGetBuffer(out var segment)) Buffer = segment.Array; }
        public new async Task<long> DownloadFileAsync(string fileId, Stream destination, long maxBytes, CancellationToken token)
        {
            Downloads++; FileId = fileId; MaxBytes = maxBytes;
            if (Hook != null) return await Hook(destination, token);
            await destination.WriteAsync(Bytes, token); Capture(destination); return Bytes.Length;
        }
    }
    private sealed class Ports : IVetPhotoArchiveStore, IVetPhotoExtractionStore, IVetPhotoDispatchStore
    {
        public List<string> Order { get; } = []; public List<Guid> ScheduledKeys { get; } = []; public List<string> ReuseVersions { get; } = [];
        public List<VetPhotoImageCompletion> Completions { get; } = [];
        public List<(string Category, VetPhotoImageFailureDisposition Disposition)> Failures { get; } = [];
        public List<(string Category, bool Transient)> DownloadFailures { get; } = [];
        public int Reservations, CurrentClaims, Reads, Dispatches, Releases;
        public bool NoOriginal, CanDispatch = true, FailureTokenCancelled, ReleaseTokenCancelled, DownloadFailureTokenCancelled;
        public Action? BeforeRead; public Func<CancellationToken, Task>? FailureWait, ReleaseWait; public byte[]? ReadBuffer, CommittedCopy; public Exception? CompletionException, DispatchException;
        public VetPhotoArchiveCommit Commit { get; private set; } = null!;
        public VetPhotoArchiveResult CommitResult { get; set; } = new(VetPhotoArchiveStatus.Retained, null);
        public VetPhotoReservation Reservation { get; set; } = new(VetPhotoArchiveStatus.Retained, null, null);
        public VetPhotoImageResult? Stored { get; set; } public VetPhotoImageResult? ClaimResult { get; set; }
        public VetPhotoImageResult ReuseResult { get; set; } = new(VetPhotoImageStatus.NotFound);
        public VetPhotoImageResult Completed { get; set; }
        public VetPhotoAdmission Admission { get; set; }
        public VetPhotoInputRevision? SelectedInput { get; set; }
        public VetPhotoImageClaim Claim { get; set; } = new(Scope, AttemptKey, ClaimToken, Now.AddMinutes(5), 222, SourceId, InputId, InputId, 1, false, null);
        public VetPhotoExtraction Extraction { get; } = new() { Id = Guid.NewGuid(), SourceId = SourceId, InputRevisionId = InputId, AttemptId = AttemptKey };
        public Ports()
        {
            Admission = new(VetPhotoAdmissionStatus.Existing, new() { Id = SourceId, SourceSlot = 1,
                FamilyId = 1, BotDbId = 2, TelegramBotId = 1001, ChatId = -100, TopicId = 7,
                SourceMessageDbId = 901, SourceAuthorUserId = 111, CurrentInputRevisionId = InputId, CurrentOrdinal = 1 }, Input(InputId, "synthetic caption"));
            SelectedInput = Admission.Input; Completed = new(VetPhotoImageStatus.Installed, Extraction: Extraction);
        }
        public VetPhotoInputRevision Input(Guid id, string caption) => new() { Id = id, SourceId = SourceId,
            FamilyId = 1, BotDbId = 2, TelegramBotId = 1001, ChatId = -100, TopicId = 7, FileId = "synthetic-file", Caption = caption };
        public VetPhotoReservation Download() => new(VetPhotoArchiveStatus.Reserved,
            new(Scope, Admission.Source!, Admission.Input!, new() { Id = Guid.NewGuid() }, Guid.NewGuid()), null);
        public Task<VetPhotoAdmission?> GetSourceAsync(VetDiaryScope scope, Guid source, CancellationToken ct)
        { Order.Add("source"); return Task.FromResult<VetPhotoAdmission?>(Admission); }
        public Task<VetPhotoImageResult?> ReadStoredImageAsync(VetDiaryScope scope, Guid source, Guid input, Guid? key, long actor, CancellationToken ct)
        { Order.Add("stored"); return Task.FromResult(Stored); }
        public Task<VetPhotoReservation> ReserveDownloadAsync(VetDiaryScope scope, Guid source, Guid input, long actor, CancellationToken ct)
        { Order.Add("reserve"); Reservations++; return Task.FromResult(Reservation); }
        public Task<VetPhotoArchiveResult> CommitOriginalAsync(VetPhotoArchiveCommit commit, CancellationToken ct)
        { Order.Add("archive_commit"); Commit = commit; CommittedCopy = commit.Original.ToArray(); return Task.FromResult(CommitResult); }
        public Task<bool> RecordDownloadFailureAsync(VetDiaryScope scope, Guid attempt, Guid token, string category, bool transient, CancellationToken ct)
        { DownloadFailureTokenCancelled = ct.IsCancellationRequested; DownloadFailures.Add((category, transient)); return Task.FromResult(true); }
        public Task<VetPhotoImageResult> ClaimCurrentImageAsync(VetDiaryScope scope, Guid source, Guid input, long actor, CancellationToken ct)
        { Order.Add("claim"); CurrentClaims++; return Task.FromResult(ClaimResult ?? new(VetPhotoImageStatus.Claimed, Claim)); }
        public Task<VetPhotoImageResult> ClaimScheduledImageAsync(VetDiaryScope scope, Guid key, long actor, CancellationToken ct)
        { Order.Add("claim"); ScheduledKeys.Add(key); return Task.FromResult(ClaimResult ?? new(VetPhotoImageStatus.Claimed, Claim)); }
        public Task<VetPhotoInputRevision?> ReadInputAsync(VetDiaryScope scope, Guid source, Guid input, long actor, CancellationToken ct)
        { Order.Add("input"); BeforeRead?.Invoke(); ct.ThrowIfCancellationRequested(); return Task.FromResult(SelectedInput); }
        public Task<VetPhotoOriginalRead?> ReadOriginalAsync(VetDiaryScope scope, Guid input, long actor, Guid attempt, Guid token, DateTimeOffset until, CancellationToken ct)
        {
            Order.Add("original"); Reads++; if (NoOriginal) return Task.FromResult<VetPhotoOriginalRead?>(null);
            ReadBuffer = Bytes.ToArray(); return Task.FromResult<VetPhotoOriginalRead?>(new(Guid.NewGuid(), InputId, Guid.NewGuid(), 1, ReadBuffer, new(2, 2, "image/png", Bytes.Length, 16)));
        }
        public Task<bool> MarkImageDispatchedAsync(VetDiaryScope scope, Guid attempt, Guid token, long actor, CancellationToken ct)
        { Order.Add("dispatch"); Dispatches++; if (DispatchException != null) throw DispatchException; return Task.FromResult(CanDispatch); }
        public Task<VetPhotoImageResult> CompleteImageAsync(VetPhotoImageCompletion completion, CancellationToken ct)
        { Completions.Add(completion); if (CompletionException != null) throw CompletionException; return Task.FromResult(Completed); }
        public async Task<bool> RecordImageFailureAsync(VetDiaryScope scope, Guid attempt, Guid token, long actor, string category, VetPhotoImageFailureDisposition disposition, CancellationToken ct)
        {
            FailureTokenCancelled = ct.IsCancellationRequested; Failures.Add((category, disposition));
            ClaimResult = new(disposition == VetPhotoImageFailureDisposition.OutcomeUnknown ? VetPhotoImageStatus.Unknown : VetPhotoImageStatus.Refused);
            if (FailureWait != null) await FailureWait(ct);
            return true;
        }
        public async Task ReleaseReaderAsync(VetDiaryScope scope, Guid id, CancellationToken ct)
        { ReleaseTokenCancelled = ct.IsCancellationRequested; Releases++; if (ReleaseWait != null) await ReleaseWait(ct); }
        public Task<VetPhotoImageResult> ReuseDisplayAsync(VetDiaryScope scope, Guid source, Guid input, long actor, string model, CancellationToken ct, string promptVersion = "photo-v1")
        { ReuseVersions.Add(promptVersion); return Task.FromResult(ReuseResult); }
        public Task<IReadOnlyList<VetPhotoWork>> GetDueAsync(long family, long bot, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VetPhotoWork>> GetRetainedDueAsync(long family, long bot, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<VetPhotoWork>>([]);
        public Task<VetPhotoImageResult> InstallCurrentExtractionAsync(VetDiaryScope scope, Guid result, long actor, CancellationToken ct) => throw new NotSupportedException();
        public Task<VetPhotoAdmission> AdmitAsync(VetDiaryScope scope, IncomingMessage message, long update, VetPhotoAttachment attachment, Guid? text, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BindMessageAsync(VetDiaryScope scope, Guid source, long message, CancellationToken ct) => throw new NotSupportedException();
        public Task<VetPhotoAdmission?> FindSourceAsync(VetDiaryScope scope, int message, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VetPhotoAdmission>> GetUnboundAsync(long family, long bot, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<VetPhotoCapacityTotals> GetCapacityAsync(VetDiaryScope scope, long actor, CancellationToken ct) => throw new NotSupportedException();
        public Task<VetPhotoOriginalDeletionResult> DeleteOriginalsAsync(VetPhotoOriginalDeletion deletion, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ReclaimAsync(long family, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
