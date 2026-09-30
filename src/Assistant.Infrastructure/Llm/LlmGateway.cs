using System.Diagnostics;
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
        // one and not an unhandled timeout exception.
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

        try
        {
            var client = _chatClients.GetClient(candidate.ProviderPrefix);
            var chatMessages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
            chatMessages.AddRange(request.Messages.Select(
                m => new ChatMessage(m.Role == LlmMessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Text) { AuthorName = m.Author }));
            var options = new ChatOptions { ModelId = candidate.Name, MaxOutputTokens = _config.MaxOutputTokens };

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCts.CancelAfter(TimeSpan.FromSeconds(_config.CallTimeoutSeconds));

            var response = await client.GetResponseAsync(chatMessages, options, callCts.Token);
            stopwatch.Stop();

            var reportedCost = response.AdditionalProperties?.TryGetValue("reported_cost_usd", out var costValue) == true
                ? costValue as decimal?
                : null;

            await RecordCallAsync(request, candidate, LlmCallOutcome.Ok,
                response.Usage?.InputTokenCount is { } i ? (int)i : null,
                response.Usage?.OutputTokenCount is { } o ? (int)o : null,
                reportedCost, stopwatch.ElapsedMilliseconds, cancellationToken);

            return (LlmResult.Answered(response.Text, candidate.Name), null);
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

            await RecordCallAsync(request, candidate, LlmCallOutcome.LimitReached, null, null, null, stopwatch.ElapsedMilliseconds, cancellationToken);
            return (null, until);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own per-call timeout tripped (callCts), not the caller's cancellation -- spec 3.1:
            // timeouts are Failed, not retried on the next candidate in M3a.
            stopwatch.Stop();
            _logger.LogWarning("LLM call to {Model} timed out after {ElapsedMs}ms", candidate.Name, stopwatch.ElapsedMilliseconds);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Timeout, null, null, null, stopwatch.ElapsedMilliseconds, cancellationToken);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError("LLM call to {Model} failed: {ExceptionType}", candidate.Name, ex.GetType().Name);
            await RecordCallAsync(request, candidate, LlmCallOutcome.Failed, null, null, null, stopwatch.ElapsedMilliseconds, cancellationToken);
            return (LlmResult.Refused(LlmRefusalReason.Failed), null);
        }
    }

    private async Task RecordCallAsync(
        LlmRequest request, ModelCatalogEntry candidate, LlmCallOutcome outcome,
        int? inputTokens, int? outputTokens, decimal? reportedCost, long durationMs, CancellationToken cancellationToken)
    {
        _db.LlmCalls.Add(new LlmCall
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
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
