using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Domain.Diagnostics;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Diagnostics;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Assistant.IntegrationTests.Diagnostics;

public sealed class DebugTraceWriterTests : IntegrationTestBase
{
    [Theory]
    [InlineData("needs_reply_false")]
    [InlineData("not_eligible")]
    [InlineData("edited_message")]
    [InlineData("context_budget_exceeded")]
    [InlineData("question_false")]
    [InlineData("dose_advice_replaced")]
    public async Task Consultation_decision_reasons_and_legacy_reasons_remain_visible(string reason)
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 262_144, 104_857_600, 256, null), clock);
        await using (services)
        {
            var start = Start(1);
            (await writer.StartAsync(start, CancellationToken.None)).ShouldBe(start.TraceId);
            await writer.AppendAsync(start.TraceId, 1, new TraceEventData("decision", "skipped", reason), CancellationToken.None);
            var row = await Db.DebugTraceEvents.AsNoTracking().SingleAsync(e => e.TraceId == start.TraceId && e.Stage == "decision");
            row.ReasonCode.ShouldBe(reason);
            row.Stage.ShouldBe("decision");
            row.Outcome.ShouldBe("skipped");
        }
    }
    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private (ServiceProvider Services, DebugTraceWriter Writer, TraceCaptureState State,
        TraceSecretRegistry Secrets) Open(TraceOptions options, TestClock clock)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TELEGRAM_MANAGER_BOT_TOKEN"] = "manager-secret",
            ["OPENAI_API_KEY"] = "key1",
            ["OPENAI_PROXY"] = "http://user:proxy-pass@localhost:8080",
            ["ConnectionStrings:Assistant"] = "Host=localhost;Database=synthetic;Username=test;Password=pass4"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(options);
        services.AddSingleton(new TraceSecretRegistry(settings));
        services.AddSingleton<TraceCaptureState>();
        services.AddSingleton<TraceRedactor>();
        services.AddSingleton<DebugTraceWriter>();
        services.AddScoped<ICurrentFamily, CurrentFamily>();
        services.AddDbContext<AssistantDbContext>(builder => AssistantDbContext.Configure(builder, ConnectionString));
        var provider = services.BuildServiceProvider();
        var secrets = provider.GetRequiredService<TraceSecretRegistry>();
        secrets.Add("role-secret");
        return (provider, provider.GetRequiredService<DebugTraceWriter>(),
            provider.GetRequiredService<TraceCaptureState>(), secrets);
    }

    private static TraceStart Start(int updateId, string? text = "synthetic input",
        long? messageId = null, long familyId = 1) =>
        new(Guid.NewGuid(), familyId, 111, 222, 7, updateId, messageId, false, text, "text", "test-build");

    [Fact]
    public async Task Disabled_capture_writes_no_trace_rows()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(false, 60, 262_144, 104_857_600, 256, null), clock);
        await using (services)
        {
            (await writer.StartAsync(Start(1), CancellationToken.None)).ShouldBeNull();
            (await Db.DebugTraces.CountAsync()).ShouldBe(0);
            (await Db.DebugTraceEvents.CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Redaction_and_cumulative_utf8_budget_keep_a_visible_completion()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 1024, 104_857_600, 16, null), clock);
        await using (services)
        {
            var start = Start(2, "key1 pass4 proxy-pass manager-secret role-secret " + new string('界', 1500));
            (await writer.StartAsync(start, CancellationToken.None)).ShouldBe(start.TraceId);
            await writer.AppendAsync(start.TraceId, 1,
                new TraceEventData("model_result", "ok", Text: string.Concat(Enumerable.Repeat("🙂", 700))), CancellationToken.None);
            await writer.AppendAsync(start.TraceId, 1,
                new TraceEventData("interaction", "completed", "normal"), CancellationToken.None);

            var root = await Db.DebugTraces.SingleAsync(t => t.Id == start.TraceId);
            root.PayloadBytes.ShouldBeLessThanOrEqualTo(1024);
            root.Truncated.ShouldBeTrue();
            root.FinalOutcome.ShouldBe("completed");
            var details = await Db.DebugTraceEvents.Where(e => e.TraceId == start.TraceId)
                .Select(e => e.DetailJson).ToListAsync();
            var joined = string.Join(" ", details);
            foreach (var secret in new[] { "key1", "pass4", "proxy-pass", "manager-secret", "role-secret" })
                joined.ShouldNotContain(secret);
            joined.ShouldContain("[REDACTED]");
            foreach (var detail in details) System.Text.Json.JsonDocument.Parse(detail).Dispose();
        }
    }

    [Fact]
    public async Task Exhausted_detail_keeps_model_links_and_delivery_metadata()
    {
        var clock = new TestClock();
        var message = new StoredMessage
        {
            BotId = 111, FamilyId = 1, ChatId = 222, TelegramMessageId = 335,
            ChatType = "private", Kind = MessageKind.Text, Text = "synthetic source",
            Raw = "{}", SentAt = clock.UtcNow, CreatedAt = clock.UtcNow
        };
        Db.Messages.Add(message);
        var call = new LlmCall
        {
            FamilyId = 1, BotId = 111, ChatId = 222, TriggerMessageId = null,
            Tier = "smart", Provider = "synthetic", Model = "synthetic-model",
            Outcome = LlmCallOutcome.Ok, Cost = 0, DurationMs = 1, CreatedAt = clock.UtcNow
        };
        Db.LlmCalls.Add(call);
        await Db.SaveChangesAsync();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 1024, 104_857_600, 16, null), clock);
        await using (services)
        {
            var start = Start(3, new string('界', 1500), message.Id);
            (await writer.StartAsync(start, CancellationToken.None)).ShouldBe(start.TraceId);
            var attemptId = Guid.NewGuid();
            await writer.AppendAsync(start.TraceId, 1,
                new TraceEventData("model_request", "attempted", "normal", AttemptId: attemptId,
                    Messages: [new TraceModelMessage("user", "synthetic model input", null)],
                    Options: new TraceModelOptions("synthetic-model", null, 100, null, null, null, null)),
                CancellationToken.None);
            await writer.AppendAsync(start.TraceId, 1,
                new TraceEventData("model_result", "ok", "normal", AttemptId: attemptId,
                    LlmCallId: call.Id, Text: "synthetic model output"), CancellationToken.None);
            await writer.AppendAsync(start.TraceId, 1,
                new TraceEventData("delivery", "sent", "normal", Text: "synthetic outgoing text",
                    Sent: true, PartIndex: 1, PartCount: 2, TelegramMessageId: 444,
                    Operation: "edit_text"), CancellationToken.None);

            var root = await Db.DebugTraces.AsNoTracking().SingleAsync(t => t.Id == start.TraceId);
            root.PayloadBytes.ShouldBeLessThanOrEqualTo(1024);
            root.Truncated.ShouldBeTrue();
            var events = await Db.DebugTraceEvents.AsNoTracking().Where(e => e.TraceId == start.TraceId)
                .OrderBy(e => e.Id).ToListAsync();
            events.Select(e => e.Stage).ShouldBe(new[] { "source", "model_request", "model_result", "delivery" });
            events[1].AttemptId.ShouldBe(attemptId);
            events[2].AttemptId.ShouldBe(attemptId);
            events[2].LlmCallId.ShouldBe(call.Id);
            events[1].PayloadBytes.ShouldBe(0);
            events[2].PayloadBytes.ShouldBe(0);

            var export = await new TraceExportService(Db).ReadByMessageIdAsync(message.Id, CancellationToken.None);
            export.ShouldNotBeNull();
            var timeline = export.Traces.Single().Events;
            timeline[2].LlmCallId.ShouldBe(call.Id);
            timeline[3].Detail.Text.ShouldBeNull();
            timeline[3].Detail.Operation.ShouldBe("edit_text");
            timeline[3].Detail.PartIndex.ShouldBe(1);
            timeline[3].Detail.PartCount.ShouldBe(2);
            timeline[3].Detail.Sent.ShouldBe(true);
            timeline[3].Detail.TelegramMessageId.ShouldBe(444);
        }
    }

    [Fact]
    public async Task Retried_update_reuses_one_trace_and_keeps_both_actual_attempts()
    {
        var clock = new TestClock();
        var options = new TraceOptions(true, 60, 4096, 104_857_600, 32, null);
        var (services, writer, state, _) = Open(options, clock);
        await using (services)
        {
            var calls = Enumerable.Range(1, 2).Select(_ => new LlmCall
            {
                FamilyId = 1, BotId = 111, ChatId = 222, Tier = "smart",
                Provider = "synthetic", Model = "synthetic-model",
                Outcome = LlmCallOutcome.Ok, Cost = 0, DurationMs = 1,
                CreatedAt = clock.UtcNow
            }).ToArray();
            Db.LlmCalls.AddRange(calls);
            await Db.SaveChangesAsync();

            var first = new TraceSession(writer, options, state, NullLogger<TraceSession>.Instance);
            var original = Start(4, "synthetic original");
            await first.StartAsync(original, CancellationToken.None);
            first.TraceId.ShouldBe(original.TraceId);
            var firstAttempt = Guid.NewGuid();
            await first.RecordAsync(new TraceEventData("model_request", "attempted", "normal",
                AttemptId: firstAttempt), CancellationToken.None);
            await first.RecordAsync(new TraceEventData("model_result", "ok", "normal",
                AttemptId: firstAttempt, LlmCallId: calls[0].Id), CancellationToken.None);

            var retry = new TraceSession(writer, options, state, NullLogger<TraceSession>.Instance);
            var retryStart = Start(4, "synthetic retry");
            await retry.StartAsync(retryStart, CancellationToken.None);
            retry.TraceId.ShouldBe(original.TraceId);
            var secondAttempt = Guid.NewGuid();
            await retry.RecordAsync(new TraceEventData("model_request", "attempted", "normal",
                AttemptId: secondAttempt), CancellationToken.None);
            await retry.RecordAsync(new TraceEventData("model_result", "ok", "normal",
                AttemptId: secondAttempt, LlmCallId: calls[1].Id), CancellationToken.None);

            (await Db.DebugTraces.CountAsync()).ShouldBe(1);
            var events = await Db.DebugTraceEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
            events.Count(e => e.Stage == "source").ShouldBe(1);
            events.Where(e => e.Stage == "model_result").Select(e => e.LlmCallId)
                .ShouldBe(new long?[] { calls[0].Id, calls[1].Id });
            events.Where(e => e.Stage == "model_result").Select(e => e.AttemptId)
                .ShouldBe(new Guid?[] { firstAttempt, secondAttempt });
            events.Single(e => e.Stage == "source").DetailJson.ShouldContain("synthetic original");
            events.Single(e => e.Stage == "source").DetailJson.ShouldNotContain("synthetic retry");
        }
    }

    [Fact]
    public async Task Expired_same_update_is_removed_without_starting_a_replacement_trace()
    {
        var clock = new TestClock();
        var old = clock.UtcNow.AddDays(-61);
        Db.DebugTraces.Add(new DebugTrace
        {
            Id = Guid.NewGuid(), FamilyId = 1, BotId = 111, ChatId = 222,
            UpdateId = 5, Kind = "text", BuildIdentity = "test-build",
            CreatedAt = old, ExpiresAt = old.AddDays(60), LastEventAt = old,
            AccountedBytes = 1024
        });
        Db.DebugTraceCoverage.Add(new DebugTraceCoverage { Id = 1, RetainedBytes = 1024 });
        await Db.SaveChangesAsync();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 4096, 104_857_600, 32, null), clock);
        await using (services)
        {
            (await writer.StartAsync(Start(5, "synthetic retry"), CancellationToken.None)).ShouldBeNull();
            (await Db.DebugTraces.CountAsync()).ShouldBe(0);
            (await Db.DebugTraceCoverage.AsNoTracking().SingleAsync()).RetainedBytes.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Admission_is_serialized_and_evicts_oldest_whole_trace()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 4096, 16_000, 32, null), clock);
        await using (services)
        {
            var first = Start(10, new string('a', 3500));
            var second = Start(11, new string('b', 3500));
            (await writer.StartAsync(first, CancellationToken.None)).ShouldBe(first.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            (await writer.StartAsync(second, CancellationToken.None)).ShouldBe(second.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            var additional = Enumerable.Range(12, 2).Select(i => Start(i, new string('c', 3500))).ToArray();
            await Task.WhenAll(additional.Select(s => writer.StartAsync(s, CancellationToken.None)));

            var rows = await Db.DebugTraces.OrderBy(t => t.CreatedAt).ToListAsync();
            rows.ShouldNotBeEmpty();
            rows.Sum(r => r.AccountedBytes).ShouldBeLessThanOrEqualTo(16_000);
            rows.ShouldNotContain(r => r.Id == first.TraceId);
            var state = await Db.DebugTraceCoverage.SingleAsync();
            state.RetainedBytes.ShouldBe(rows.Sum(r => r.AccountedBytes));
            state.EvictedCount.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Admission_waits_for_the_database_lock_then_enforces_the_cap()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 4096, 12_000, 32, null), clock);
        await using (services)
        {
            var first = Start(16, new string('a', 3500));
            var second = Start(17, new string('b', 3500));
            (await writer.StartAsync(first, CancellationToken.None)).ShouldBe(first.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            (await writer.StartAsync(second, CancellationToken.None)).ShouldBe(second.TraceId);

            await using var blocker = new NpgsqlConnection(ConnectionString);
            await blocker.OpenAsync();
            await using var lockTx = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = lockTx;
                // The writer uses this application-specific PostgreSQL transaction lock key.
                command.CommandText = "SELECT pg_advisory_xact_lock(4778953959271449413)";
                await command.ExecuteNonQueryAsync();
            }

            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            var third = Start(18, new string('c', 3500));
            var admission = writer.StartAsync(third, CancellationToken.None);
            var waiterObserved = false;
            await using (var observer = new NpgsqlConnection(ConnectionString))
            {
                await observer.OpenAsync();
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    await using var query = observer.CreateCommand();
                    query.CommandText = """
                        SELECT count(*) FROM pg_locks
                        WHERE locktype = 'advisory' AND NOT granted
                          AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                        """;
                    waiterObserved = (long)(await query.ExecuteScalarAsync() ?? 0L) > 0;
                    if (waiterObserved) break;
                    await Task.Delay(20);
                }
            }
            waiterObserved.ShouldBeTrue("a second connection must reach the advisory lock");
            admission.IsCompleted.ShouldBeFalse();

            await lockTx.CommitAsync();
            (await admission).ShouldBe(third.TraceId);
            var rows = await Db.DebugTraces.OrderBy(t => t.CreatedAt).ToListAsync();
            rows.Sum(t => t.AccountedBytes).ShouldBeLessThanOrEqualTo(12_000);
            rows.ShouldNotContain(t => t.Id == first.TraceId);
            rows.ShouldContain(t => t.Id == third.TraceId);
        }
    }

    [Fact]
    public async Task Cross_family_append_cannot_change_another_familys_trace()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 4096, 104_857_600, 32, null), clock);
        await using (services)
        {
            var start = Start(19, familyId: 1);
            (await writer.StartAsync(start, CancellationToken.None)).ShouldBe(start.TraceId);
            var before = await Db.DebugTraces.AsNoTracking().SingleAsync(t => t.Id == start.TraceId);

            await writer.AppendAsync(start.TraceId, 2,
                new TraceEventData("answer", "generated", "normal", Text: "foreign synthetic text"),
                CancellationToken.None);

            var after = await Db.DebugTraces.AsNoTracking().SingleAsync(t => t.Id == start.TraceId);
            after.EventCount.ShouldBe(before.EventCount);
            after.PayloadBytes.ShouldBe(before.PayloadBytes);
            (await Db.DebugTraceEvents.CountAsync(e => e.TraceId == start.TraceId)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Append_eviction_never_skips_the_oldest_active_root_or_resurrects_it()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 4096, 12_000, 32, null), clock);
        await using (services)
        {
            var first = Start(14, new string('a', 3500));
            var second = Start(15, new string('b', 3500));
            (await writer.StartAsync(first, CancellationToken.None)).ShouldBe(first.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            (await writer.StartAsync(second, CancellationToken.None)).ShouldBe(second.TraceId);

            await writer.AppendAsync(first.TraceId, 1,
                new TraceEventData("model_result", "ok", Text: new string('c', 1000)), CancellationToken.None);
            await writer.AppendAsync(first.TraceId, 1,
                new TraceEventData("interaction", "completed", "normal"), CancellationToken.None);

            (await Db.DebugTraces.AnyAsync(t => t.Id == first.TraceId)).ShouldBeFalse();
            (await Db.DebugTraces.AnyAsync(t => t.Id == second.TraceId)).ShouldBeTrue();
            (await Db.DebugTraceCoverage.SingleAsync()).EvictedCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Dropped_append_does_not_evict_newer_traces_to_make_room_for_its_payload()
    {
        var clock = new TestClock();
        var (services, writer, _, _) = Open(new TraceOptions(true, 60, 8192, 13_000, 32, null), clock);
        await using (services)
        {
            var active = Start(43, "a");
            var newerOne = Start(44, new string('b', 3000));
            var newerTwo = Start(45, new string('c', 3000));
            (await writer.StartAsync(active, CancellationToken.None)).ShouldBe(active.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            (await writer.StartAsync(newerOne, CancellationToken.None)).ShouldBe(newerOne.TraceId);
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            (await writer.StartAsync(newerTwo, CancellationToken.None)).ShouldBe(newerTwo.TraceId);

            await writer.AppendAsync(active.TraceId, 1,
                new TraceEventData("model_result", "ok", "normal", Text: new string('d', 6000)),
                CancellationToken.None);

            var rows = await Db.DebugTraces.AsNoTracking().ToListAsync();
            rows.Count.ShouldBe(2);
            rows.ShouldContain(t => t.Id == newerOne.TraceId);
            rows.ShouldContain(t => t.Id == newerTwo.TraceId);
            rows.Sum(t => t.AccountedBytes).ShouldBeLessThanOrEqualTo(13_000);
            (await Db.DebugTraceCoverage.AsNoTracking().SingleAsync()).EvictedCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Disabled_cleanup_expires_trace_without_deleting_ordinary_message()
    {
        var clock = new TestClock();
        var message = new StoredMessage
        {
            BotId = 111, FamilyId = 1, ChatId = 222, TopicId = 7,
            TelegramMessageId = 333, ChatType = "private", Kind = MessageKind.Text,
            Text = "synthetic input", Raw = "{}", SentAt = clock.UtcNow, CreatedAt = clock.UtcNow
        };
        Db.Messages.Add(message);
        await Db.SaveChangesAsync();

        var (enabledServices, enabledWriter, _, _) = Open(new TraceOptions(true, 60, 1024, 104_857_600, 16, null), clock);
        await using (enabledServices)
        {
            (await enabledWriter.StartAsync(Start(20, messageId: message.Id), CancellationToken.None)).ShouldNotBeNull();
        }
        clock.UtcNow = clock.UtcNow.AddDays(61);
        var (disabledServices, disabledWriter, _, _) = Open(new TraceOptions(false, 60, 1024, 104_857_600, 16, null), clock);
        await using (disabledServices)
            await disabledWriter.CleanupAsync(CancellationToken.None);

        (await Db.DebugTraces.CountAsync()).ShouldBe(0);
        (await Db.Messages.CountAsync(m => m.Id == message.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Expiry_batch_commit_survives_a_cancelled_later_batch()
    {
        var clock = new TestClock();
        var message = new StoredMessage
        {
            BotId = 111, FamilyId = 1, ChatId = 222, TelegramMessageId = 336,
            ChatType = "private", Kind = MessageKind.Text, Text = "synthetic input",
            Raw = "{}", SentAt = clock.UtcNow, CreatedAt = clock.UtcNow
        };
        Db.Messages.Add(message);
        await Db.SaveChangesAsync();
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = 1, BotId = 111, ChatId = 222, TriggerMessageId = message.Id,
            Tier = "smart", Provider = "synthetic", Model = "synthetic-model",
            Outcome = LlmCallOutcome.Ok, Cost = 0, DurationMs = 1, CreatedAt = clock.UtcNow
        });
        var old = clock.UtcNow.AddDays(-61);
        Db.DebugTraces.AddRange(Enumerable.Range(1, 205).Select(i => new DebugTrace
        {
            Id = Guid.NewGuid(), FamilyId = 1, BotId = 111, ChatId = 222,
            UpdateId = i, Kind = "text", BuildIdentity = "test-build",
            CreatedAt = old.AddSeconds(i), ExpiresAt = old.AddDays(60).AddSeconds(i),
            LastEventAt = old.AddSeconds(i), AccountedBytes = 1024
        }));
        Db.DebugTraceCoverage.Add(new DebugTraceCoverage { Id = 1, RetainedBytes = 205 * 1024 });
        await Db.SaveChangesAsync();

        var (services, writer, _, _) = Open(new TraceOptions(false, 60, 1024, 104_857_600, 16, null), clock);
        await using (services)
        {
            (await writer.CleanupBatchAsync(CancellationToken.None)).ShouldBeTrue();
            (await Db.DebugTraces.CountAsync()).ShouldBe(105);
            (await Db.DebugTraceCoverage.AsNoTracking().SingleAsync()).RetainedBytes.ShouldBe(105 * 1024);

            await using var blocker = new NpgsqlConnection(ConnectionString);
            await blocker.OpenAsync();
            await using var lockTx = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = lockTx;
                command.CommandText = "SELECT pg_advisory_xact_lock(4778953959271449413)";
                await command.ExecuteNonQueryAsync();
            }
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await writer.CleanupBatchAsync(cancelled.Token));
            await lockTx.CommitAsync();

            (await Db.DebugTraces.CountAsync()).ShouldBe(105);
            (await Db.DebugTraceCoverage.AsNoTracking().SingleAsync()).RetainedBytes.ShouldBe(105 * 1024);
            (await Db.Messages.CountAsync(m => m.Id == message.Id)).ShouldBe(1);
            (await Db.LlmCalls.CountAsync()).ShouldBe(1);
            await writer.CleanupAsync(CancellationToken.None);
            (await Db.DebugTraces.CountAsync()).ShouldBe(0);
            (await Db.DebugTraceCoverage.AsNoTracking().SingleAsync()).RetainedBytes.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Failed_diagnostic_insert_does_not_poison_business_context()
    {
        var clock = new TestClock();
        var (services, writer, state, _) = Open(new TraceOptions(true, 60, 1024, 104_857_600, 16, null), clock);
        await using (services)
        {
            var session = new TraceSession(writer,
                new TraceOptions(true, 60, 1024, 104_857_600, 16, null),
                state, NullLogger<TraceSession>.Instance);
            await session.StartAsync(Start(30, messageId: 999999), CancellationToken.None);
            session.Enabled.ShouldBeFalse();

            Db.Messages.Add(new StoredMessage
            {
                BotId = 111, FamilyId = 1, ChatId = 222, TelegramMessageId = 334,
                ChatType = "private", Kind = MessageKind.Text, Text = "later synthetic input",
                Raw = "{}", SentAt = clock.UtcNow, CreatedAt = clock.UtcNow
            });
            await Db.SaveChangesAsync();
            (await Db.Messages.CountAsync(m => m.TelegramMessageId == 334)).ShouldBe(1);
            (await Db.DebugTraces.CountAsync()).ShouldBe(0);
        }
    }
}
