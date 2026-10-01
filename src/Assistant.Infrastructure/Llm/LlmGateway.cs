using System.Diagnostics;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Llm;

public class LlmGateway : ILlmGateway
{
    private readonly LlmConfig _config;
    private readonly ModelCatalog _catalog;
    private readonly IModelAvailability _availability;
    private readonly IChatClientProvider _chatClients;
    private readonly AssistantDbContext _db;
    private readonly IClock _clock;
    private readonly ConcurrentCallGate _concurrencyGate;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(
        LlmConfig config,
        ModelCatalog catalog,
        IModelAvailability availability,
        IChatClientProvider chatClients,
        AssistantDbContext db,
        IClock clock,
        ConcurrentCallGate concurrencyGate,
        ILogger<LlmGateway> logger)
    {
        _config = config;
        _catalog = catalog;
        _availability = availability;
        _chatClients = chatClients;
        _db = db;
        _clock = clock;
        _concurrencyGate = concurrencyGate;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public IReadOnlyList<ModelStatus> DescribeModels() =>
        _catalog.Models.Select(m => new ModelStatus(m.Name, _availability.IsAvailable(m.Name), _availability.RetryAt(m.Name))).ToArray();

    public bool IsKnownModel(string name) => _catalog.TryGetByName(name, out _);

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        // Spec §8.8: take the concurrency semaphore FIRST, then check the rate guard. A single slot
        // covers the whole call including any fallback attempts across candidates -- fallback happens
        // serially within one guarded slot, it does not claim a second slot per attempt. If the wait
        // for a free slot exceeds LLM_CALL_TIMEOUT_SECONDS, that's a RateLimited refusal, not a Failed
        // one and not an unhandled timeout exception. Note: because the wait and the call each get up
        // to LLM_CALL_TIMEOUT_SECONDS, a call that waits almost the full timeout for a slot and then
        // succeeds can make CompleteAsync's total wall time approach 2x the configured call timeout --
        // this is intentional (a RateLimited refusal is only reported once the wait itself times out).
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(TimeSpan.FromSeconds(_config.CallTimeoutSeconds));
        try
        {
            await _concurrencyGate.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LlmResult.Refused(LlmRefusalReason.RateLimited);
        }

        try
        {
            // Spec §8.8: count llm_calls ATTEMPT rows -- every row this family has written, including
            // fallback attempts across models -- in a sliding 60 s window and the current UTC day.
            // This count-then-check is not atomic against concurrent calls for the same family: two
            // overlapping CompleteAsync calls can both read the count before either has recorded its
            // own row, so the per-family limit can be overshot slightly. How far it can be overshot is
            // bounded by LLM_MAX_CONCURRENT_CALLS (the process-wide _concurrencyGate above) -- at most
            // that many calls are ever in flight at once, so at most that many can race this check.
            var minuteCutoff = _clock.UtcNow.AddMinutes(-1);
            var callsLastMinute = await _db.LlmCalls.IgnoreQueryFilters()
                .CountAsync(c => c.FamilyId == request.FamilyId && c.CreatedAt >= minuteCutoff, cancellationToken);
            if (callsLastMinute >= _config.CallsPerMinute)
            {
                return LlmResult.Refused(LlmRefusalReason.RateLimited);
            }

            var dayCutoff = new DateTimeOffset(_clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var callsToday = await _db.LlmCalls.IgnoreQueryFilters()
                .CountAsync(c => c.FamilyId == request.FamilyId && c.CreatedAt >= dayCutoff, cancellationToken);
            if (callsToday >= _config.CallsPerDay)
            {
                return LlmResult.Refused(LlmRefusalReason.DailyCapReached);
            }

            var candidates = _catalog.GetCandidateOrder(request.Tier, request.PreferredModel);
            DateTimeOffset? earliestRetry = null;

            foreach (var candidate in candidates)
            {
                if (!_availability.IsAvailable(candidate.Name))
                {
                    var unavailableUntil = _availability.RetryAt(candidate.Name);
                    if (unavailableUntil is { } until && (earliestRetry is null || until < earliestRetry))
                    {
                        earliestRetry = until;
                    }

                    continue;
                }

                var attempt = await TryCandidateAsync(request, candidate, cancellationToken);
                if (attempt.Retry is { } retryAt)
                {
                    if (earliestRetry is null || retryAt < earliestRetry)
                    {
                        earliestRetry = retryAt;
                    }

                    continue;
                }

                return attempt.Result!;
            }

            return LlmResult.Refused(LlmRefusalReason.AllModelsUnavailable, earliestRetry);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private async Task<(LlmResult? Result, DateTimeOffset? Retry)> TryCandidateAsync(
        LlmRequest request, ModelCatalogEntry candidate, CancellationToken cancellationToken)
    {
        // The concurrency slot is already held for the whole CompleteAsync call (see above) -- this
        // method does NOT acquire/release it per candidate.
        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;

        try
        {
            var client = _chatClients.GetClient(candidate.ProviderPrefix, candidate.Name);
            var chatMessages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
            chatMessages.AddRange(request.Messages.Select(
                m => new ChatMessage(m.Role == LlmMessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Text) { AuthorName = m.Author }));
            var options = new ChatOptions { ModelId = candidate.Name, MaxOutputTokens = _config.MaxOutputTokens };

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCts.CancelAfter(TimeSpan.FromSeconds(_config.CallTimeoutSeconds));

            response = await client.GetResponseAsync(chatMessages, options, callCts.Token);
        }
        catch (ModelLimitReachedException ex)
        {
            stopwatch.Stop();
            var until = ex.RetryAt ?? _clock.UtcNow.AddMinutes(_config.ModelCooldownMinutes);

            // Spec §8.7: a session/weekly/spend/usage-limit message is account-wide -- it marks
            // EVERY catalog entry for this provider unavailable, not just the one that was called. A
            // model-named limit ("Opus limit"/"Sonnet limit") marks only the entry that failed.
            if (ex.Scope == LlmLimitScope.Provider)
            {
                foreach (var sameProviderEntry in _catalog.Models.Where(m => m.ProviderPrefix == candidate.ProviderPrefix))
                {
                    _availability.MarkUnavailable(sameProviderEntry.Name, until);
                }
            }
            else
            {
                _availability.MarkUnavailable(candidate.Name, until);
            }

            await RecordCallAsync(request, candidate, LlmCallOutcome.LimitReached, null, null, null, stopwatch.ElapsedMilliseconds);

            // ModelAvailability never shortens an existing mark (a longer cooldown from an earlier
            // failure wins), so the retry we report must reflect what actually got recorded, not the
            // `until` we just computed -- otherwise a caller could be told to retry earlier than the
            // model will really be available again.
            var effectiveRetry = _availability.RetryAt(candidate.Name) ?? until;
            return (null, effectiveRetry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (e.g. process shutdown), not our own per-call timeout below --
            // propagate immediately: no Error log, no attempt row, no result to return. The caller's
            // own cancellation handling is responsible for whatever happens next.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own per-call timeout tripped (callCts), not the caller's cancellation -- spec 3.1:
            // timeouts are Failed, not retried on the next candidate in M3a.
            stopwatch.Stop();
            _logger.LogWarning("LLM call to {Model} timed out after {ElapsedMs}ms", candidate.Name, stopwatch.ElapsedMilliseconds);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Timeout, null, null, null, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }
        catch (TimeoutException)
        {
            // Finding N5: ClaudeCliChatClient throws a plain TimeoutException when ProcessRunner's
            // own process timer fires (ProcessRunResult.TimedOut), which is a distinct code path
            // from this method's own callCts timeout above but means the same thing to a caller --
            // record it the same way (Timeout outcome), not as a generic Failed with no useful
            // outcome.
            stopwatch.Stop();
            _logger.LogWarning("LLM call to {Model} timed out after {ElapsedMs}ms", candidate.Name, stopwatch.ElapsedMilliseconds);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Timeout, null, null, null, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError("LLM call to {Model} failed: {ExceptionType}", candidate.Name, ex.GetType().Name);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }

        // Everything from here on is OUTSIDE the try above, on purpose: the provider call already
        // succeeded, so a failure recording the attempt (e.g. a transient DB error) must never turn a
        // real answer into a Failed result -- see RecordCallAsync.
        stopwatch.Stop();

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }

        var reportedCost = ExtractReportedCost(response);
        await RecordCallAsync(
            request, candidate, LlmCallOutcome.Ok,
            response.Usage?.InputTokenCount is { } i ? (int)i : null,
            response.Usage?.OutputTokenCount is { } o ? (int)o : null,
            reportedCost, stopwatch.ElapsedMilliseconds);

        return (LlmResult.Answered(response.Text, candidate.Name), null);
    }

    /// <summary>Reads the provider-reported cost defensively: it may arrive as a <c>decimal</c> (set
    /// directly in-process, e.g. by <c>ClaudeCliChatClient</c>), a <c>double</c>, or a
    /// <c>JsonElement</c> (if the response round-tripped through JSON serialization). Anything else
    /// is treated as "no cost reported" rather than throwing.</summary>
    private static decimal? ExtractReportedCost(ChatResponse response)
    {
        if (response.AdditionalProperties?.TryGetValue(LlmResponseKeys.ReportedCostUsd, out var costValue) != true)
        {
            return null;
        }

        return costValue switch
        {
            decimal d => d,
            double d => (decimal)d,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDecimal(out var jd) => jd,
            _ => null
        };
    }

    private async Task RecordCallAsync(
        LlmRequest request, ModelCatalogEntry candidate, LlmCallOutcome outcome,
        int? inputTokens, int? outputTokens, decimal? reportedCost, long durationMs)
    {
        var call = new LlmCall
        {
            FamilyId = request.FamilyId,
            BotId = request.BotId,
            Tier = request.Tier,
            Provider = candidate.ProviderPrefix,
            Model = candidate.Name,
            Outcome = outcome,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ReportedCost = reportedCost,
            DurationMs = durationMs,
            CreatedAt = _clock.UtcNow
        };
        _db.LlmCalls.Add(call);

        try
        {
            // Never tied to the caller's token: by the time we get here the provider call is done
            // (succeeded or definitively failed) -- recording the attempt is best-effort bookkeeping
            // that should not be aborted by a cancellation racing the end of the call.
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // An attempt row is bookkeeping, not the result: a DB failure here must never surface as
            // (or turn into) a different outcome than what the provider actually returned. Log the
            // exception TYPE only -- never ex.Message, which could contain persisted data -- and
            // detach the entity so this failed row is not retried by a later SaveChangesAsync call
            // on the same (scoped) DbContext.
            _logger.LogError("Failed to record LLM call attempt: {ExceptionType}", ex.GetType().Name);
            _db.Entry(call).State = EntityState.Detached;
        }
    }
}
