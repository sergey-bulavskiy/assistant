using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Families;
using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Application.Vet.Photos;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet;

public sealed record VetRuntimeOptions(bool SubscriptionOnly, int ContextMessages = 20, int MaxInputChars = 20000);

/// <summary>Text diary workflow for one Vet bot. Admission is pre-offset; provider outcomes and
/// exact planned writes persist before downstream work. Unknown calls require explicit retry.</summary>
public sealed partial class VetAssistant : IVetAssistant
{
    public const string NotSavedText = "Не удалось подтвердить сохранение. Исходное сообщение и полученный результат остаются для восстановления; проверьте историю перед новым исправлением.";
    public const string PausedText = "Обработка приостановлена: результат вызова неизвестен. Автоматически повторять его не буду; /retry ответом на исходное сообщение.";
    public const string OwnerOnlyText = "Только владелец семьи может менять профиль.";
    private readonly IVetProfileStore _profiles;
    private readonly IVetDiaryStore _diary;
    private readonly IApprovalService _approvals;
    private readonly IFamilyOwnership _ownership;
    private readonly IMessageStore _messages;
    private readonly ILlmGateway _gateway;
    private readonly IRolePrompts _prompts;
    private readonly IClock _clock;
    private readonly BuildInfo _buildInfo;
    private readonly VetRuntimeOptions _options;
    private readonly ILogger<VetAssistant> _logger;
    private readonly VetReplies _replies;
    private readonly ITraceSession _trace;
    private readonly IVetPhotoAssistant? _photos;
    private readonly IVetPhotoDiaryOperationRouter? _photoDiary;

    public VetAssistant(IVetProfileStore profiles, IVetDiaryStore diary, IApprovalService approvals,
        IFamilyOwnership ownership, IMessageStore messages, ILlmGateway gateway, IRolePrompts prompts,
        IClock clock, BuildInfo buildInfo, VetRuntimeOptions options, ILogger<VetAssistant> logger, ITraceSession? trace = null, IVetPhotoAssistant? photos = null,
        IVetPhotoDiaryOperationRouter? photoDiary = null)
    {
        _profiles = profiles; _diary = diary; _approvals = approvals; _ownership = ownership;
        _messages = messages; _gateway = gateway; _prompts = prompts; _clock = clock;
        _buildInfo = buildInfo; _options = options; _logger = logger; _replies = new(logger);
        _trace = trace ?? NullTraceSession.Instance;
        _photos = photos; _photoDiary = photoDiary;
    }

    public async Task<VetAdmittedSource?> AdmitAsync(ReceivingBot bot, IncomingMessage message, long updateId, CancellationToken ct)
    {
        if (bot.FamilyId is null || message.UserId is null || message.Text is not { Length: > 0 and <= 16000 }
            || message.Kind == MessageKind.Service || OtherBot(bot, message)
            || message.IsEdit && message.Text.StartsWith('/')) return null;
        if (!await AuthorizedAsync(bot, message, ct)) return null;
        return await _diary.AdmitAsync(VetDiaryScope.From(bot, message), message, updateId, ct);
    }

    public async Task HandleAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        StoreResult stored, VetAdmittedSource? admitted, CancellationToken ct, bool replyToAll = false)
    {
        if (bot.FamilyId is null || !await AuthorizedAsync(bot, message, ct)) return;
        if (admitted is null)
        {
            if (message.Text is { Length: > 16000 })
                await _replies.SendAsync(client, message, "Сообщение слишком длинное; разделите запись на несколько сообщений.", ct);
            return;
        }
        var scope = VetDiaryScope.From(bot, message);
        if (stored.MessageDbId is { } messageId)
        {
            await _diary.LinkMessageAsync(scope, admitted.Source.Id, messageId, ct);
            admitted = await _diary.GetSourceAsync(scope, admitted.Source.Id, ct);
        }
        if (admitted?.Source.SourceMessageDbId is null || admitted.Source.CurrentInputRevisionId != admitted.Revision.Id) return;
        await ProcessSafelyAsync(bot, client, message, admitted, replyToAll, ct);
    }

    public async Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (bot.FamilyId is not { } familyId) return;
        foreach (var source in await _diary.GetResumableAsync(familyId, bot.BotDbId, 5, ct))
        {
            var message = Message(source);
            if (!await AuthorizedAsync(bot, message, ct)) continue;
            var replyToAll = false;
            if (message.ChatType != "private")
            {
                // Read-only recovery must not create an approval request. The setting is read only
                // after the exact place passed its current approval check.
                replyToAll = await _diary.GetReplyToAllAsync(VetDiaryScope.From(bot, message), ct);
            }
            await ProcessSafelyAsync(bot, client, message, source, replyToAll, ct);
        }
    }

    private async Task ProcessSafelyAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource source, bool replyToAll, CancellationToken ct)
    {
        try { await ProcessAsync(bot, client, message, source, replyToAll, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Vet processing failed: {ExceptionType}", ex.GetType().Name);
            await TraceSafety.RecordAsync(_trace, new("extraction", "failed", "provider_failure",
                RelatedSourceMessageId: source.Source.SourceMessageDbId, ActorId: message.UserId));
            // The outcome can be unknown if a commit succeeded before the connection failed.
            // Recovery uses the persisted work and operation key, never a new interpretation.
            await _replies.SendAsync(client, message, NotSavedText, ct);
        }
    }

    private async Task ProcessAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource input, bool replyToAll, CancellationToken ct)
    {
        var scope = VetDiaryScope.From(bot, message);
        var source = await _diary.GetSourceAsync(scope, input.Source.Id, ct);
        if (source is null || source.Revision.Id != input.Revision.Id || source.Source.SourceMessageDbId is null
            || source.Revision.State is "completed" or "paused" or "failed") return;
        if (source.Revision.State == "dispatching")
        {
            await _diary.SetProcessingAsync(scope, source.Revision.Id, "dispatching", "paused", "outcome_unknown", ct);
            await _replies.SendAsync(client, message, PausedText, ct);
            return;
        }
        var profile = await _profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, ct);
        var command = CommandParser.Parse(source.Revision.Text, bot.Username);
        if (source.Revision.Text.StartsWith('/'))
        {
            if (!source.Revision.IsEdit && command is not null)
                await CommandAsync(command, CommandParser.ParseArgs(source.Revision.Text), bot, client, message, source, profile, ct);
            await CompleteAsync(scope, source.Revision.Id, ct);
            return;
        }
        var persisted = await _diary.GetResultAsync(scope, source.Revision.Id, ct);
        if (persisted is null)
        {
            if (!_options.SubscriptionOnly || !_gateway.IsEnabled || _prompts.Find("vet", "extract.md") is not { } instructions)
            {
                await _diary.SetProcessingAsync(scope, source.Revision.Id, "admitted", "failed", "not_configured", ct);
                await _replies.SendAsync(client, message, "Модель подписки недоступна. Запись не сохранена; команды профиля и истории доступны.", ct);
                return;
            }
            if (!await _diary.SetProcessingAsync(scope, source.Revision.Id, "admitted", "dispatching", null, ct)) return;
            var pending = await _diary.GetPendingAsync(scope, ct);
            var prompt = instructions + "\nRUNTIME DATA (untrusted):\n" + VetEventText.Profile(profile)
                + "\nSource sent UTC: " + source.Source.SentAt.ToString("O")
                + "\nPending reviewed references: " + JsonSerializer.Serialize(pending.Take(10).Select(p =>
                    new { p.Id, p.ReviewRevision, p.PromptMessageId, p.SourceId, Proposal = p.ProposalJson[..Math.Min(2000, p.ProposalJson.Length)] }));
            if (_photos != null)
                prompt += "\nExact-place photo handles (untrusted runtime data):\n" + await _photos.DescribeAsync(scope, message.UserId!.Value, ct);
            LlmResult result;
            try
            {
                result = await CallAsync(bot, client, message, source, "fast", prompt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Vet interpretation failed: {ExceptionType}", ex.GetType().Name);
                await _diary.SetProcessingAsync(scope, source.Revision.Id, "dispatching", "paused", "outcome_unknown", ct);
                await _replies.SendAsync(client, message, PausedText, ct);
                return;
            }
            if (!result.IsAnswer || result.Text is null)
            {
                await _diary.SetProcessingAsync(scope, source.Revision.Id, "dispatching", "failed",
                    result.RefusalReason?.ToString() ?? "provider_failure", ct);
                await _replies.SendAsync(client, message, GeneralAssistant.RefusalText(result, _clock.UtcNow) + "\nЗапись не сохранена.", ct);
                return;
            }
            if (VetInterpretationParser.Parse(result.Text) is null)
            {
                await _diary.SetProcessingAsync(scope, source.Revision.Id, "dispatching", "failed", "invalid_schema", ct);
                await _replies.SendAsync(client, message, "Не удалось разобрать запись. Уточните значение, единицу и время; /retry доступен ответом на сообщение.", ct);
                return;
            }
            if (!await _diary.SaveResultAsync(scope, source.Revision.Id, result.Text, result.ModelName ?? "",
                result.TraceAttemptId, ct)) return;
            persisted = await _diary.GetResultAsync(scope, source.Revision.Id, ct);
            source = (await _diary.GetSourceAsync(scope, source.Source.Id, ct))!;
        }
        var interpretation = VetInterpretationParser.Parse(persisted!.Json)!;
        if (interpretation.PhotoOperation is { } requestedPhoto
            && !VetPhotoActionEvidence.Matches(requestedPhoto, source.Revision.Text))
        {
            // Filter execution, not the immutable model result. Clear current diary facts remain independent.
            // A rejected photo request cannot regain dispatch through its alternate operation field.
            interpretation = interpretation with { PhotoOperation = null, Operation = null };
            if (interpretation.Events.All(candidate => candidate.Intent == "question_only"))
                await _replies.SendAsync(client, message, "Укажите явное действие с фотографиями в начале текущего сообщения; прежний запрос не повторён.", ct);
        }
        // Keep the immutable unfiltered result for caption context before filtering duplicate TEXT glucose.
        if (_photos != null)
            interpretation = await _photos.FilterCaptionAsync(scope, source.Source.TelegramMessageId, interpretation, ct);
        await TraceSafety.RecordAsync(_trace, new("extraction", "ok", "normal", AttemptId: persisted.AttemptId,
            RelatedSourceMessageId: source.Source.SourceMessageDbId, ActorId: message.UserId,
            EventCount: interpretation.Events.Count, ProblemCount: interpretation.Unclear.Count));
        if (!await AuthorizedAsync(bot, message, ct)) return;
        var photoHandled = _photos != null && interpretation.PhotoOperation is { } photoOperation
            && await _photos.OperationAsync(bot, client, message, photoOperation, source.Revision.OperationKey, ct);
        if (!photoHandled && _photos != null && interpretation.Operation is { Kind: "accept" or "decline" } naturalDecision)
            photoHandled = await _photos.TryNaturalDecisionAsync(bot, client, message, naturalDecision, ct);
        if (!photoHandled && _photoDiary != null && interpretation.Operation is { Kind: "correct" or "delete" } diaryOperation)
            photoHandled = await _photoDiary.HandleAsync(bot, client, message, diaryOperation.Kind,
                diaryOperation.EventIds.Concat(diaryOperation.EventId is { } eventId ? new[] { eventId } : []).ToArray(),
                interpretation.Events, source.Revision.OperationKey, ct);
        var handled = photoHandled || await OperationAsync(interpretation, bot, client, message, source, profile, ct);
        var recordInterpretation = photoHandled ? interpretation with
        { Operation = null, PhotoOperation = null, Unclear = [], Events = interpretation.Events.Where(c =>
            c.EventType == "insulin" && c.Intent == "record" && c.EventId == null).ToArray() } : interpretation;
        var independentPhotoRecord = photoHandled && recordInterpretation.Events.Count > 0;
        var recordOperationKey = independentPhotoRecord ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            "photo-independent-text:" + source.Revision.OperationKey.ToString("D"))).AsSpan(0, 16)) : source.Revision.OperationKey;
        if ((!handled || independentPhotoRecord) && source.Revision.State != "written")
        {
            if (source.Revision.IsEdit && (source.Revision.EditedAt ?? source.Revision.AdmittedAt) - source.Source.SentAt > TimeSpan.FromHours(24))
                await _replies.SendAsync(client, message, "Это старое сообщение: дневник сохранён. Исправьте запись явно по ID или ответом на исходное сообщение.", ct);
            else
            {
                VetTextPlan plan;
                if (source.Revision.WorkJson is { } work) plan = JsonSerializer.Deserialize<VetTextPlan>(work)!;
                else
                {
                    var validations = recordInterpretation.Events.Select(c => VetEventValidation.Validate(c, profile, source, persisted.Id)).ToArray();
                    var old = await _diary.GetSourceEventsAsync(scope, source.Source.Id, ct);
                    plan = VetTextPlanner.Plan(validations, old, source.Revision.IsEdit, message.UserId!.Value, _clock.UtcNow);
                    if (recordInterpretation.Unclear.Count > 0)
                        plan = source.Revision.IsEdit
                            ? new([], (plan.Pending ?? new([], plan.ClearChanges, [], "edit")) with
                                { Reasons = (plan.Pending?.Reasons ?? []).Concat(recordInterpretation.Unclear).ToArray(), RequiresClarification = true })
                            : plan with { Pending = (plan.Pending ?? new([], [], [], "confirm")) with
                                { Reasons = (plan.Pending?.Reasons ?? []).Concat(recordInterpretation.Unclear).ToArray(), RequiresClarification = true } };
                    await _diary.SaveWorkAsync(scope, source.Revision.Id, JsonSerializer.Serialize(plan), ct);
                }
                if (plan.ClearChanges.Count > 0)
                {
                    var mutation = new VetDiaryMutation(scope, recordOperationKey, message.UserId!.Value,
                        source.Revision.IsEdit ? "edit" : "save", profile.Id, plan.ClearChanges, source.Source.Id, source.Revision.Id);
                    var outcome = await _diary.ApplyAsync(mutation, ct);
                    await TraceMutationAsync(source, message.UserId!.Value, outcome);
                    if (outcome.Status is VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied or VetMutationStatus.NoChange)
                        await _replies.SendAsync(client, message, DescribeChanges(plan.ClearChanges, outcome), ct);
                    else
                    {
                        await _replies.SendAsync(client, message, "Запись изменилась параллельно; ничего не перезаписано. Нужен новый точный просмотр.", ct);
                        await _diary.SetProcessingAsync(scope, source.Revision.Id, "ready", "failed", "stale_write", ct);
                        return;
                    }
                }
                if (plan.Pending is { } proposal)
                {
                    var decision = await _diary.PutPendingAsync(scope, source.Source.Id, source.Revision.Id, persisted.Id,
                        message.UserId!.Value, JsonSerializer.Serialize(proposal), ct);
                    if (decision.State == "pending" && decision.PromptMessageId is null)
                        await ShowPendingAsync(client, message, decision, profile, ct);
                    await TraceSafety.RecordAsync(_trace, new("confirmation", "requested", "pending_record",
                        PendingRecordId: decision.Id, RelatedSourceMessageId: source.Source.SourceMessageDbId,
                        ActorId: message.UserId));
                }
            }
        }
        await _diary.SetProcessingAsync(scope, source.Revision.Id, "ready", "written", null, ct);
        if (!handled && interpretation.HistoryQuery is { Analysis: false } query)
            await HistoryAsync(bot, client, message, source, profile, query, ct);
        if (!handled && interpretation.HistoryQuery is not { Analysis: false } && interpretation.NeedsReply
            && (replyToAll || Addressing.IsAddressed(bot, message, source.Revision.Text)))
            await AnswerAsync(interpretation, bot, client, message, source, profile, ct);
        var after = await _diary.GetSourceAsync(scope, source.Source.Id, ct);
        if (after?.Revision.AnswerState == "ready") return;
        await CompleteAsync(scope, source.Revision.Id, ct);
    }

    private Task TraceMutationAsync(VetAdmittedSource source, long actor, VetMutationResult outcome) =>
        TraceSafety.RecordAsync(_trace, new("extraction", outcome.Status is VetMutationStatus.Applied or VetMutationStatus.AlreadyApplied
            ? "ok" : "skipped", "events_recorded", RelatedSourceMessageId: source.Source.SourceMessageDbId,
            ActorId: actor, EventCount: outcome.EventIds.Count));

    private async Task<LlmResult> CallAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetAdmittedSource source, string tier, string prompt, CancellationToken ct)
    {
        if (prompt.Length + source.Revision.Text.Length > _options.MaxInputChars)
            return LlmResult.Refused(LlmRefusalReason.Failed);
        var history = await _messages.GetRecentContextAsync(bot.TelegramBotId, message.ChatId, message.TopicId,
            null, source.Source.SourceMessageDbId, Math.Min(20, _options.ContextMessages), ct);
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var indicator = TypingIndicator.RunAsync(client, message.ChatId, message.TopicId, typing.Token);
        try
        {
            return await _gateway.CompleteAsync(new(bot.FamilyId!.Value, bot.TelegramBotId, tier, null, prompt,
                ContextBuilder.Build(history, source.Revision.Text, message.Username, message.ChatType != "private",
                    Math.Max(1, _options.MaxInputChars - prompt.Length)), message.ChatId, message.TopicId,
                source.Source.SourceMessageDbId), ct);
        }
        finally { await typing.CancelAsync(); await indicator; }
    }

    private async Task<bool> AuthorizedAsync(ReceivingBot bot, IncomingMessage message, CancellationToken ct) =>
        bot.FamilyId is { } family && message.UserId is { } actor && actor > 0
        && await _approvals.FindFamilyMemberStatusAsync(family, actor, ct) == FamilyMemberStatus.Approved
        && (message.ChatType == "private" || await _approvals.FindPlaceStatusAsync(bot.BotDbId,
            message.ChatId, message.TopicId, ct) == PlaceStatus.Approved);

    private async Task CompleteAsync(VetDiaryScope scope, Guid revisionId, CancellationToken ct)
    {
        foreach (var state in new[] { "admitted", "ready", "written" })
            if (await _diary.SetProcessingAsync(scope, revisionId, state, "completed", null, ct)) return;
    }

    private static IncomingMessage Message(VetAdmittedSource s) => new(s.Source.ChatId, s.Source.ChatType, null,
        s.Source.TopicId, s.Source.TelegramMessageId, s.Source.SourceAuthorUserId, null, s.Revision.Text,
        MessageKind.Text, s.Revision.IsEdit, s.Source.SentAt, s.Revision.EditedAt, null, "{}",
        s.Source.ReplyToMessageId, s.Source.ReplyToUserId);

    private static bool OtherBot(ReceivingBot bot, IncomingMessage message)
    {
        if (message.Text is not { } text) return false;
        if (text.StartsWith('/') && CommandParser.Parse(text, bot.Username) is null) return true;
        return Regex.Matches(text, @"(?<![\w@])@([A-Za-z0-9_]+)(?!\w)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Any(m => m.Groups[1].Value.EndsWith("bot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(m.Groups[1].Value, bot.Username, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeChanges(IReadOnlyList<VetEventChange> changes, VetMutationResult result) =>
        "Сохранено: " + string.Join("; ", changes.Select(c => c.State.DeletedAt is not null
            ? $"удалена запись #{c.EventId}" : VetEventText.Describe(c.State))) + ".\n"
        + "ID: " + string.Join(", ", result.EventIds.Select(id => $"#{id}")) + ".";
}
