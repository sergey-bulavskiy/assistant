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
    private readonly IBudgetGuard _budget;
    private readonly IBudgetNoticeDispatcher _budgetNoticeDispatcher;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(
        LlmConfig config,
        ModelCatalog catalog,
        IModelAvailability availability,
        IChatClientProvider chatClients,
        AssistantDbContext db,
        IClock clock,
        ConcurrentCallGate concurrencyGate,
        IBudgetGuard budget,
        IBudgetNoticeDispatcher budgetNoticeDispatcher,
        ILogger<LlmGateway> logger)
    {
        _config = config;
        _catalog = catalog;
        _availability = availability;
        _chatClients = chatClients;
        _db = db;
        _clock = clock;
        _concurrencyGate = concurrencyGate;
        _budget = budget;
        _budgetNoticeDispatcher = budgetNoticeDispatcher;
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

        LlmResult result;
        var budgetWasConfigured = false;

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

            // Budget guard (platform-wide money budget), after the semaphore and the rate guards.
            // With no budget configured budgetStatus is null and nothing below restricts anything.
            // Spec §10.3/review: notices are NOT sent from this pre-call status -- see the post-call
            // check after this try/finally, which re-evaluates against the call's own recorded cost.
            var candidates = _catalog.GetCandidateOrder(request.Tier, request.PreferredModel);
            var budgetStatus = await _budget.EvaluateAsync(cancellationToken);
            budgetWasConfigured = budgetStatus is not null;

            var filtered = budgetStatus is null ? candidates : ApplyBudgetFilter(candidates, budgetStatus.Overall);

            // The earliest moment a budget skip could stop applying (the binding period's reset);
            // set whenever at least one candidate was skipped for budget, filtered out or estimated.
            DateTimeOffset? budgetResetAt = filtered.Count < candidates.Count ? ComputeBudgetResetAt(candidates, budgetStatus!) : null;

            if (filtered.Count == 0)
            {
                // Guard refusal: no call, no llm_calls row.
                result = LlmResult.Refused(LlmRefusalReason.BudgetExhausted, budgetResetAt);
            }
            else
            {
                DateTimeOffset? earliestRetry = null;
                LlmResult? loopResult = null;

                foreach (var candidate in filtered)
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

                    var price = GetPrice(candidate);
                    var estimate = LlmCostCalculator.Estimate(request.SystemPrompt.Length, InputChars(request), _config.MaxOutputTokens, price);

                    if (budgetStatus is not null && !LlmCostCalculator.IsZero(price) && ExceededPeriod(budgetStatus, estimate) is { } exceeded)
                    {
                        // Pre-call estimate would push a period's spend above its hard cap: skip this
                        // candidate (no call, no row) and try the next one.
                        //
                        // This check is itself a pre-call estimate, not a running total: concurrent
                        // calls can each pass it before any of their own rows land, the same race the
                        // rate guard above has. How far spend can overshoot the hard cap this way is
                        // bounded by LLM_MAX_CONCURRENT_CALLS x this candidate's own estimate -- at most
                        // that many calls are ever in flight at once, so at most that many can race this
                        // check before a row exists to make the next one fail it. The hard%'s headroom
                        // above 100% (e.g. the default 120%) exists to absorb exactly this.
                        if (budgetResetAt is null || exceeded.PeriodEnd < budgetResetAt)
                        {
                            budgetResetAt = exceeded.PeriodEnd;
                        }

                        continue;
                    }

                    var attempt = await TryCandidateAsync(request, candidate, price, estimate, cancellationToken);
                    if (attempt.Retry is { } retryAt)
                    {
                        if (earliestRetry is null || retryAt < earliestRetry)
                        {
                            earliestRetry = retryAt;
                        }

                        continue;
                    }

                    loopResult = attempt.Result!;
                    break;
                }

                // A remaining, budget-allowed candidate that is merely unavailable (a limit mark) wins
                // over a budget skip: it is only temporarily unavailable, so it could still answer once
                // its own retry time passes, and reporting BudgetExhausted would hide that and (wrongly)
                // tell the caller only the budget's reset can help. BudgetExhausted is reported only
                // when the budget filter/estimate removed every candidate that could ever answer this
                // period -- i.e. no remaining candidate's unavailability is the thing actually standing
                // in the way.
                result = loopResult ?? (earliestRetry is { } retry
                    ? LlmResult.Refused(LlmRefusalReason.AllModelsUnavailable, retry)
                    : budgetResetAt is { } resetAt
                        ? LlmResult.Refused(LlmRefusalReason.BudgetExhausted, resetAt)
                        : LlmResult.Refused(LlmRefusalReason.AllModelsUnavailable, null));
            }
        }
        finally
        {
            _concurrencyGate.Release();
        }

        // Spec §10.3/review: notices are checked AFTER the call's cost is recorded (not from the
        // pre-call status used for filtering above) and OFF the reply path entirely -- never
        // awaited here, and never using this request's own scoped `_db` (the dispatcher creates its
        // own DI scope, its own AssistantDbContext). Only when a budget was actually configured:
        // with none configured there is nothing to ever cross. `Dispatch()` itself never throws
        // synchronously (it only starts a Task that catches everything internally), but the
        // discard-and-swallow shape below is kept anyway as the same defense-in-depth every other
        // best-effort path here already has -- this must never turn an already-decided `result`
        // into something else.
        if (budgetWasConfigured)
        {
            try
            {
                _ = _budgetNoticeDispatcher.Dispatch();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to start the post-call budget notice check: {ExceptionType}", ex.GetType().Name);
            }
        }

        return result;
    }

    private ModelPrice GetPrice(ModelCatalogEntry entry) =>
        _config.Prices.TryGetValue(entry.Name, out var price) ? price : new ModelPrice(0m, 0m);

    // Everything sent as this call's input besides the system prompt: the whole context window.
    private static int InputChars(LlmRequest request) => request.Messages.Sum(m => m.Text.Length);

    /// <summary>The period whose hard cap (limit x hard%) the estimate would push spend above, or
    /// null; the monthly one first, since its reset is the later one.</summary>
    private static BudgetPeriodStatus? ExceededPeriod(BudgetStatus status, decimal estimate) =>
        status.Monthly.Spend + estimate > status.Monthly.HardCap ? status.Monthly
        : status.Daily.Spend + estimate > status.Daily.HardCap ? status.Daily
        : null;

    /// <summary>When the initial budget filter removed at least one candidate, the moment that
    /// stops applying. <see cref="BudgetStatus.Binding"/> only looks at severity: the monthly period
    /// whenever it is at least as severe as the overall state, otherwise the daily one. That is
    /// right except in one case -- daily strictly more severe than monthly (daily is "binding") --
    /// where a day reset only actually helps if the monthly period's own state, applied by itself,
    /// still leaves a candidate; if it would filter out everything just the same, report the
    /// month's reset instead of promising relief the day's reset can't deliver.</summary>
    private DateTimeOffset ComputeBudgetResetAt(IReadOnlyList<ModelCatalogEntry> candidates, BudgetStatus status)
    {
        var binding = status.Binding;
        if (binding.Kind == BudgetNotice.MonthlyPeriod)
        {
            return binding.PeriodEnd;
        }

        var monthAlone = ApplyBudgetFilter(candidates, status.Monthly.State);
        return monthAlone.Count > 0 ? binding.PeriodEnd : status.Monthly.PeriodEnd;
    }

    /// <summary>Normal/Warn: unchanged. Soft: the chat's current preference first (if Soft allows it
    /// at all -- fast tier or zero price), then every LLM_FAST_MODELS entry in that variable's own
    /// declared order (not the main chain's -- the owner may rank them differently there), then
    /// every zero-price entry in chain order; if that leaves nothing, behave as Hard. Hard: only
    /// zero-price entries, in chain order.</summary>
    private IReadOnlyList<ModelCatalogEntry> ApplyBudgetFilter(IReadOnlyList<ModelCatalogEntry> candidates, BudgetState state)
    {
        if (state is BudgetState.Normal or BudgetState.Warn)
        {
            return candidates;
        }

        var zeroPrice = candidates.Where(c => LlmCostCalculator.IsZero(GetPrice(c))).ToArray();
        if (state == BudgetState.Hard)
        {
            return zeroPrice;
        }

        var fastNames = _config.FastModels.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateNames = candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsAllowed(ModelCatalogEntry c) => fastNames.Contains(c.Name) || LlmCostCalculator.IsZero(GetPrice(c));

        var ordered = new List<ModelCatalogEntry>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // candidates[0] is already whatever GetCandidateOrder put first (the chat's real preference,
        // if one was set and known) -- keep it first here too, but only if Soft actually allows it.
        if (candidates.Count > 0 && IsAllowed(candidates[0]) && placed.Add(candidates[0].Name))
        {
            ordered.Add(candidates[0]);
        }

        foreach (var fast in _config.FastModels)
        {
            if (candidateNames.Contains(fast.Name) && placed.Add(fast.Name))
            {
                ordered.Add(fast);
            }
        }

        foreach (var zero in zeroPrice)
        {
            if (placed.Add(zero.Name))
            {
                ordered.Add(zero);
            }
        }

        return ordered.Count > 0 ? ordered : zeroPrice;
    }

    private async Task<(LlmResult? Result, DateTimeOffset? Retry)> TryCandidateAsync(
        LlmRequest request, ModelCatalogEntry candidate, ModelPrice price, decimal estimate, CancellationToken cancellationToken)
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

            // Cost 0: the provider rejected the call before any billable work.
            await RecordCallAsync(request, candidate, LlmCallOutcome.LimitReached, null, null, null, 0m, stopwatch.ElapsedMilliseconds);

            // ModelAvailability never shortens an existing mark (a longer cooldown from an earlier
            // failure wins), so the retry we report must reflect what actually got recorded, not the
            // `until` we just computed -- otherwise a caller could be told to retry earlier than the
            // model will really be available again.
            var effectiveRetry = _availability.RetryAt(candidate.Name) ?? until;
            return (null, effectiveRetry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (e.g. process shutdown), not our own per-call timeout below.
            // Cancellation for a zero-price legacy entry propagates immediately. Codex retains the dispatched attempt
            // for source accounting even though its monetary cost is zero. A PAID entry may be doing
            // billable work that we'll never see the usage for, so the attempt is still charged the
            // pre-call estimate -- recorded with CancellationToken.None (detaching the row on failure,
            // same as every other RecordCallAsync call) since the caller's own token is already
            // cancelled and must not also abort this bookkeeping write. Either way: no Error log, no
            // result to return, and the caller's own cancellation handling is responsible for whatever
            // happens next.
            stopwatch.Stop();
            if (!LlmCostCalculator.IsZero(price) || candidate.ProviderPrefix == LlmProviderValidation.CodexCliPrefix)
            {
                await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, estimate, stopwatch.ElapsedMilliseconds);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own per-call timeout tripped (callCts), not the caller's cancellation -- spec 3.1:
            // timeouts are Failed, not retried on the next candidate in M3a.
            stopwatch.Stop();
            _logger.LogWarning("LLM call to {Model} timed out after {ElapsedMs}ms", candidate.Name, stopwatch.ElapsedMilliseconds);
            // No usage came back, so the pre-call estimate is recorded as the cost (0 for zero-price).
            await RecordCallAsync(request, candidate, LlmCallOutcome.Timeout, null, null, null, estimate, stopwatch.ElapsedMilliseconds);
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
            // No usage came back, so the pre-call estimate is recorded as the cost (0 for zero-price).
            await RecordCallAsync(request, candidate, LlmCallOutcome.Timeout, null, null, null, estimate, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError("LLM call to {Model} failed: {ExceptionType}", candidate.Name, ex.GetType().Name);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, estimate, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }

        // Everything from here on is OUTSIDE the try above, on purpose: the provider call already
        // succeeded, so a failure recording the attempt (e.g. a transient DB error) must never turn a
        // real answer into a Failed result -- see RecordCallAsync.
        stopwatch.Stop();

        // The provider answered: cost comes from its usage (0 for a zero-price entry). An empty
        // answer is still billed -- usage x price when usage came back, otherwise the estimate.
        var inputTokens = response.Usage?.InputTokenCount;
        var outputTokens = response.Usage?.OutputTokenCount;
        decimal cost;
        if (inputTokens is null && outputTokens is null)
        {
            // Neither side reported: nothing to go on at all, so bill the whole pre-call estimate.
            cost = estimate;
        }
        else
        {
            // Partial usage: a missing INPUT count falls back to the same estimated input tokens the
            // pre-call estimate used (never 0 -- that would understate a real, billable input that
            // merely wasn't reported back). A missing OUTPUT count is billed as 0, not the configured
            // max -- an output count only goes missing when there is no output to meter, since the
            // provider call already succeeded and returned a response.
            var effectiveInputTokens = inputTokens is { } reportedInputTokens
                ? (decimal)reportedInputTokens
                : LlmCostCalculator.EstimatedInputTokens(request.SystemPrompt.Length, InputChars(request));
            cost = LlmCostCalculator.Compute(effectiveInputTokens, outputTokens ?? 0, price);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, cost, stopwatch.ElapsedMilliseconds);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }

        var reportedCost = ExtractReportedCost(response);
        await RecordCallAsync(
            request, candidate, LlmCallOutcome.Ok,
            inputTokens is { } i ? (int)i : null,
            outputTokens is { } o ? (int)o : null,
            reportedCost, cost, stopwatch.ElapsedMilliseconds);

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
        int? inputTokens, int? outputTokens, decimal? reportedCost, decimal cost, long durationMs)
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
            Cost = cost,
            DurationMs = durationMs,
            CreatedAt = _clock.UtcNow,
            ChatId = request.ChatId,
            TopicId = request.TopicId,
            TriggerMessageId = request.TriggerMessageId
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
