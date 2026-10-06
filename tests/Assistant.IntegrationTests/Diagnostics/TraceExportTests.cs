using System.Diagnostics;
using System.Reflection;
using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Domain.Diagnostics;
using Assistant.Domain.Families;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Diagnostics;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.IntegrationTests.Diagnostics;

public sealed class TraceExportTests : IntegrationTestBase
{
    private const long BotId = 111;
    private const long ChatId = 222;
    private const int TelegramMessageId = 333;

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task Export_reports_bounded_missing_interactions_in_the_selected_context_without_their_content()
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var family = new Family { Name = "synthetic family", CreatedAt = now };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        var source = Incoming(family.Id, BotId, ChatId, TelegramMessageId, "synthetic source", now.AddMinutes(-10));
        source.EditedAt = now.AddMinutes(-2);
        Db.Messages.Add(source);
        await Db.SaveChangesAsync();
        var root = Root(family.Id, source.Id, BotId, ChatId, 61, false, "message", source.CreatedAt);
        Db.DebugTraces.Add(root);
        for (var index = 0; index < 66; index++)
            Db.Messages.Add(Incoming(family.Id, BotId, ChatId, 400 + index,
                "untraced private synthetic marker", now.AddMinutes(-8).AddSeconds(index)));
        // Neither an earlier interaction nor another bot/chat belongs to this observation window.
        Db.Messages.AddRange(
            Incoming(family.Id, BotId, ChatId, 500, "earlier private synthetic marker", now.AddMinutes(-20)),
            Incoming(family.Id, BotId + 1, ChatId, 501, "foreign private synthetic marker", now.AddMinutes(-7)),
            Incoming(family.Id, BotId, ChatId + 1, 502, "foreign private synthetic marker", now.AddMinutes(-7)));
        await Db.SaveChangesAsync();
        Db.DebugTraceEvents.Add(Event(root, now.AddMinutes(-1), "delivery", "sent",
            "{\"text\":\"synthetic edited prompt\",\"operation\":\"edit_text\",\"telegram_message_id\":900}", null));
        await Db.SaveChangesAsync();

        var document = await new TraceExportService(Db).ReadByMessageIdAsync(source.Id, CancellationToken.None);

        document.ShouldNotBeNull();
        document.Coverage.ObservedFrom.ShouldBe(root.CreatedAt);
        document.Coverage.ObservedMissingInteractionCount.ShouldBe(67);
        document.Coverage.ObservedGaps.Count.ShouldBe(64);
        document.Coverage.OmittedGapCount.ShouldBe(3);
        document.Coverage.ObservedGaps[0].From.ShouldBe(now.AddMinutes(-8));
        document.Coverage.ObservedGaps.ShouldAllBe(g => g.From == g.Through && g.Evidence == "capture_not_observed");
        document.Traces.Single().Events.Single().Detail.Operation.ShouldBe("edit_text");
        var json = System.Text.Json.JsonSerializer.Serialize(document);
        json.ShouldNotContain("untraced private synthetic marker");
        json.ShouldNotContain("foreign private synthetic marker");
        json.ShouldNotContain("earlier private synthetic marker");
        document.CoverageNote.ShouldContain("cause is unknown");
    }

    [Fact]
    public async Task Export_reads_writer_persisted_attempt_and_confirmation_links()
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var family = new Family { Name = "synthetic family", CreatedAt = now };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        var source = Incoming(family.Id, BotId, ChatId, TelegramMessageId,
            "synthetic source text", now);
        Db.Messages.Add(source);
        await Db.SaveChangesAsync();
        var call = Call(family.Id, source.Id, BotId, ChatId, "synthetic-model", now);
        Db.LlmCalls.Add(call);
        await Db.SaveChangesAsync();

        var services = new ServiceCollection();
        var clock = new FixedClock(now);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(new Assistant.Infrastructure.Diagnostics.TraceOptions(true, 60, 262_144, 104_857_600, 256, null));
        services.AddSingleton(new TraceSecretRegistry(new ConfigurationBuilder().Build()));
        services.AddSingleton<TraceCaptureState>();
        services.AddSingleton<TraceRedactor>();
        services.AddSingleton<DebugTraceWriter>();
        services.AddScoped<ICurrentFamily, CurrentFamily>();
        services.AddDbContext<AssistantDbContext>(builder => AssistantDbContext.Configure(builder, ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<DebugTraceWriter>();

        var originalId = Guid.NewGuid();
        (await writer.StartAsync(new TraceStart(originalId, family.Id, BotId, ChatId, null,
            51, source.Id, false, "synthetic source text", "message"), CancellationToken.None)).ShouldBe(originalId);
        var attemptId = Guid.NewGuid();
        await writer.AppendAsync(originalId, family.Id,
            new TraceEventData("model_result", "ok", "normal", AttemptId: attemptId,
                LlmCallId: call.Id, Text: "synthetic generated text"), CancellationToken.None);

        clock.UtcNow = now.AddSeconds(1);
        var callbackId = Guid.NewGuid();
        (await writer.StartAsync(new TraceStart(callbackId, family.Id, BotId, ChatId, null,
            52, null, false, null, "callback"), CancellationToken.None)).ShouldBe(callbackId);
        await writer.AppendAsync(callbackId, family.Id,
            new TraceEventData("confirmation", "accepted", "normal", PendingRecordId: 777,
                RelatedSourceMessageId: source.Id, ActorId: 555), CancellationToken.None);

        var document = await new TraceExportService(Db).ReadByMessageIdAsync(source.Id, CancellationToken.None);
        document.ShouldNotBeNull();
        document.Traces.Select(t => t.TraceId).ShouldBe(new[] { originalId, callbackId });
        var savedAttempt = document.Traces[0].Events.Single(e => e.Stage == "model_result");
        savedAttempt.AttemptId.ShouldBe(attemptId);
        savedAttempt.LlmCallId.ShouldBe(call.Id);
        document.LlmCalls.Single().Id.ShouldBe(call.Id);
        var savedConfirmation = document.Traces[1].Events.Single(e => e.Stage == "confirmation");
        savedConfirmation.RelatedSourceMessageId.ShouldBe(source.Id);
        savedConfirmation.PendingRecordId.ShouldBe(777);
        savedConfirmation.ActorId.ShouldBe(555);
    }

    [Fact]
    public async Task Both_selectors_export_only_original_edit_and_related_confirmation_with_actual_call_links()
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL stores microseconds, not 100 ns ticks.
        var family = new Family { Name = "synthetic family", CreatedAt = now };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();

        var source = Incoming(family.Id, BotId, ChatId, TelegramMessageId, "current source text", now);
        var foreign = Incoming(family.Id, BotId, ChatId, TelegramMessageId + 1, "unrelated source text", now);
        Db.Messages.AddRange(source, foreign);
        await Db.SaveChangesAsync();

        var linkedCall = Call(family.Id, source.Id, BotId, ChatId, "synthetic-model", now.AddSeconds(2));
        var foreignCall = Call(family.Id, foreign.Id, BotId, ChatId, "foreign-model", now.AddSeconds(3));
        Db.LlmCalls.AddRange(linkedCall, foreignCall);
        await Db.SaveChangesAsync();

        var original = Root(family.Id, source.Id, BotId, ChatId, 41, false, "message", now);
        var edit = Root(family.Id, source.Id, BotId, ChatId, 42, true, "edited_message", now.AddMinutes(1));
        var confirmation = Root(family.Id, null, BotId, ChatId, 43, false, "callback", now.AddMinutes(2));
        var unrelated = Root(family.Id, foreign.Id, BotId, ChatId, 44, false, "message", now.AddMinutes(3));
        Db.DebugTraces.AddRange(original, edit, confirmation, unrelated);
        await Db.SaveChangesAsync();

        Db.DebugTraceEvents.AddRange(
            Event(original, now, "model_request", "attempted", "{\"messages\":[{\"role\":\"user\",\"text\":\"historical prompt\"}]}", null),
            Event(original, now.AddSeconds(2), "model_result", "ok", "{\"text\":\"synthetic generated reply\"}", linkedCall.Id),
            Event(original, now.AddSeconds(3), "answer", "not_sent", "{\"text\":\"synthetic rejected reply\",\"sent\":false}", null),
            Event(original, now.AddSeconds(4), "delivery", "sent", "{\"text\":\"synthetic replacement\",\"sent\":true,\"telegram_message_id\":900}", null),
            Event(edit, now.AddMinutes(1), "decision", "skipped", "{}", null),
            Event(confirmation, now.AddMinutes(2), "confirmation", "accepted", "{}", null, source.Id, 555, 777),
            Event(unrelated, now.AddMinutes(3), "model_result", "ok", "{\"text\":\"foreign reply\"}", foreignCall.Id));
        Db.DebugTraceCoverage.Add(new DebugTraceCoverage
        {
            Id = 1,
            RetainedBytes = 9000,
            EvictedCount = 2,
            LastEvictedAt = now.AddMinutes(-5)
        });
        await Db.SaveChangesAsync();

        var service = new TraceExportService(Db);
        var byId = await service.ReadByMessageIdAsync(source.Id, CancellationToken.None);
        var byTelegram = await service.ReadByTelegramReferenceAsync(
            BotId, ChatId, TelegramMessageId, CancellationToken.None);

        byId.ShouldNotBeNull();
        byTelegram.ShouldNotBeNull();
        byId.SourceMessageId.ShouldBe(source.Id);
        byTelegram.SourceMessageId.ShouldBe(source.Id);
        byTelegram.Traces.Select(t => t.TraceId).ShouldBe(new[] { original.Id, edit.Id, confirmation.Id });
        byId.Traces.Select(t => t.UpdateId).ShouldBe(new long?[] { 41, 42, 43 });
        byId.Traces.Select(t => t.IsEdit).ShouldBe(new[] { false, true, false });
        byId.Traces[2].Events.Single().RelatedSourceMessageId.ShouldBe(source.Id);
        byId.Traces[2].Events.Single().PendingRecordId.ShouldBe(777);
        byId.Traces[2].Events.Single().ActorId.ShouldBe(555);
        byId.Traces[0].Events.Select(e => e.Stage).ShouldBe(
            new[] { "model_request", "model_result", "answer", "delivery" });
        byId.Traces[0].Events[0].Detail.Messages!.Single().Text.ShouldBe("historical prompt");
        byId.Traces[0].Events[2].Outcome.ShouldBe("not_sent");
        byId.Traces[0].Events[2].Detail.Sent.ShouldBe(false);
        byId.Traces[0].Events[3].Detail.Text.ShouldBe("synthetic replacement");
        byId.Traces[0].Events[3].Detail.TelegramMessageId.ShouldBe(900);
        byId.LlmCalls.Select(c => c.Id).ShouldBe(new[] { linkedCall.Id });
        byId.LlmCalls.Single().Model.ShouldBe("synthetic-model");
        byId.Coverage.RetainedTraceCount.ShouldBe(4);
        byId.Coverage.AccountedBytes.ShouldBe(9000);
        byId.Coverage.CapEvictionCount.ShouldBe(2);
        byId.Coverage.OldestRetainedAt.ShouldBe(original.CreatedAt);
        byId.Coverage.NewestRetainedAt.ShouldBe(unrelated.CreatedAt);
        byId.CoverageNote.ShouldContain("gaps");
    }

    [Fact]
    public async Task Missing_source_or_source_without_trace_returns_no_document()
    {
        var now = DateTimeOffset.UtcNow;
        var family = new Family { Name = "synthetic family", CreatedAt = now };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        var source = Incoming(family.Id, BotId, ChatId, TelegramMessageId, "synthetic text", now);
        Db.Messages.Add(source);
        await Db.SaveChangesAsync();

        var service = new TraceExportService(Db);
        (await service.ReadByMessageIdAsync(source.Id, CancellationToken.None)).ShouldBeNull();
        (await service.ReadByMessageIdAsync(source.Id + 999, CancellationToken.None)).ShouldBeNull();
        (await service.ReadByTelegramReferenceAsync(BotId + 1, ChatId, TelegramMessageId,
            CancellationToken.None)).ShouldBeNull();
        (await service.ReadByTelegramReferenceAsync(BotId, ChatId, TelegramMessageId + 1,
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void Export_path_requires_a_new_absolute_file_outside_any_git_repository()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"assistant-trace-{Guid.NewGuid():N}.json");
        var existing = Path.GetTempFileName();
        try
        {
            TraceExportPath.TryResolve(outside, out var accepted).ShouldBeTrue();
            accepted.ShouldBe(Path.GetFullPath(outside));
            File.Exists(outside).ShouldBeFalse();

            TraceExportPath.TryResolve(null, out _).ShouldBeFalse();
            TraceExportPath.TryResolve("", out _).ShouldBeFalse();
            TraceExportPath.TryResolve("relative-trace.json", out _).ShouldBeFalse();
            TraceExportPath.TryResolve(existing, out _).ShouldBeFalse();

            var repoRoot = FindRepoRoot();
            var forbidden = Path.Combine(repoRoot, "tests", $"trace-{Guid.NewGuid():N}.json");
            TraceExportPath.TryResolve(forbidden, out _).ShouldBeFalse();
            File.Exists(forbidden).ShouldBeFalse();
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Fact]
    public async Task Cli_writes_only_to_explicit_file_and_not_found_creates_no_file()
    {
        var now = DateTimeOffset.UtcNow;
        var family = new Family { Name = "synthetic family", CreatedAt = now };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        var source = Incoming(family.Id, BotId, ChatId, TelegramMessageId, "synthetic source secret marker", now);
        Db.Messages.Add(source);
        await Db.SaveChangesAsync();

        var absentPath = Path.Combine(Path.GetTempPath(), $"assistant-trace-absent-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"assistant-trace-export-{Guid.NewGuid():N}.json");
        var existingPath = Path.GetTempFileName();
        var repositoryPath = Path.Combine(FindRepoRoot(), "tests", $"trace-{Guid.NewGuid():N}.json");
        try
        {
            var absent = await RunCliAsync("--message-id", source.Id.ToString(), "--out", absentPath);
            absent.ExitCode.ShouldBe(1);
            absent.StandardError.ShouldContain("not found");
            absent.StandardOutput.ShouldNotContain("synthetic source secret marker");
            File.Exists(absentPath).ShouldBeFalse();

            var trace = Root(family.Id, source.Id, BotId, ChatId, 91, false, "message", now);
            Db.DebugTraces.Add(trace);
            Db.DebugTraceEvents.Add(Event(trace, now, "answer", "not_sent",
                "{\"text\":\"synthetic rejected answer\",\"sent\":false}", null));
            await Db.SaveChangesAsync();

            var exported = await RunCliAsync("--bot-id", BotId.ToString(), "--chat-id", ChatId.ToString(),
                "--telegram-message-id", TelegramMessageId.ToString(), "--out", outputPath);
            exported.ExitCode.ShouldBe(0);
            exported.StandardOutput.ShouldContain("Exported 1 trace");
            exported.StandardOutput.ShouldNotContain("synthetic rejected answer");
            exported.StandardError.ShouldNotContain("synthetic rejected answer");
            File.Exists(outputPath).ShouldBeTrue();
            var json = await File.ReadAllTextAsync(outputPath);
            json.ShouldContain("synthetic rejected answer");
            json.ShouldContain("not_sent");
            json.ShouldContain(trace.Id.ToString());

            await File.WriteAllTextAsync(existingPath, "synthetic existing file");
            var existing = await RunCliAsync("--message-id", source.Id.ToString(), "--out", existingPath);
            existing.ExitCode.ShouldBe(2);
            (await File.ReadAllTextAsync(existingPath)).ShouldBe("synthetic existing file");
            existing.StandardOutput.ShouldNotContain("synthetic rejected answer");
            existing.StandardError.ShouldNotContain("synthetic rejected answer");

            var repository = await RunCliAsync("--message-id", source.Id.ToString(), "--out", repositoryPath);
            repository.ExitCode.ShouldBe(2);
            File.Exists(repositoryPath).ShouldBeFalse();
            repository.StandardOutput.ShouldNotContain("synthetic rejected answer");
            repository.StandardError.ShouldNotContain("synthetic rejected answer");
        }
        finally
        {
            if (File.Exists(absentPath)) File.Delete(absentPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(existingPath)) File.Delete(existingPath);
            if (File.Exists(repositoryPath)) File.Delete(repositoryPath);
        }
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(params string[] args)
    {
        var repoRoot = FindRepoRoot();
        var configuration = typeof(TraceExportTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var executable = Path.Combine(repoRoot, "src", "Assistant.TraceExport", "bin", configuration,
            "net10.0", "Assistant.TraceExport.dll");
        File.Exists(executable).ShouldBeTrue();

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(executable);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__Assistant"] = ConnectionString;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Export process failed to start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
            directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return directory.FullName;
        }

        throw new InvalidOperationException("Test project is not inside a Git checkout.");
    }

    private static StoredMessage Incoming(long familyId, long botId, long chatId,
        int telegramMessageId, string text, DateTimeOffset now) => new()
    {
        FamilyId = familyId,
        BotId = botId,
        ChatId = chatId,
        TelegramMessageId = telegramMessageId,
        ChatType = "private",
        Kind = MessageKind.Text,
        Direction = MessageDirection.In,
        Text = text,
        Raw = "{}",
        SentAt = now,
        CreatedAt = now
    };

    private static LlmCall Call(long familyId, long sourceId, long botId, long chatId,
        string model, DateTimeOffset now) => new()
    {
        FamilyId = familyId,
        BotId = botId,
        ChatId = chatId,
        TriggerMessageId = sourceId,
        Tier = "smart",
        Provider = "synthetic-provider",
        Model = model,
        Outcome = LlmCallOutcome.Ok,
        Cost = 0,
        DurationMs = 10,
        CreatedAt = now
    };

    private static DebugTrace Root(long familyId, long? sourceId, long botId, long chatId,
        long updateId, bool isEdit, string kind, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        BotId = botId,
        ChatId = chatId,
        UpdateId = updateId,
        SourceMessageId = sourceId,
        IsEdit = isEdit,
        Kind = kind,
        BuildIdentity = "synthetic-build",
        CreatedAt = now,
        ExpiresAt = now.AddDays(60),
        LastEventAt = now,
        AccountedBytes = 1024
    };

    private static DebugTraceEvent Event(DebugTrace trace, DateTimeOffset now, string stage,
        string outcome, string detailJson, long? callId, long? relatedSourceId = null,
        long? actorId = null, long? pendingId = null) => new()
    {
        TraceId = trace.Id,
        CreatedAt = now,
        Stage = stage,
        Outcome = outcome,
        DetailJson = detailJson,
        LlmCallId = callId,
        RelatedSourceMessageId = relatedSourceId,
        ActorId = actorId,
        PendingRecordId = pendingId,
        PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(detailJson),
        AccountedBytes = 1024 + System.Text.Encoding.UTF8.GetByteCount(detailJson)
    };
}
