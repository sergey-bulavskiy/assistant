using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>The safety-rule steps shared by the parts of the health assistant: evaluating new
/// events against the profile's rules (with the earlier events the combination rule may pair with),
/// and the fixed alerts for saved events (claimed in safety_alerts before they are sent). Never model
/// text; logs rule keys, levels and ids only.</summary>
internal sealed class HealthSafety
{
    private readonly IHealthProfileStore _profiles;
    private readonly IEventStore _events;
    private readonly ISafetyAlertStore _safetyAlerts;
    private readonly IClock _clock;
    private readonly HealthReplies _replies;
    private readonly ILogger _logger;

    public HealthSafety(
        IHealthProfileStore profiles, IEventStore events, ISafetyAlertStore safetyAlerts, IClock clock, HealthReplies replies, ILogger logger)
    {
        _profiles = profiles;
        _events = events;
        _safetyAlerts = safetyAlerts;
        _clock = clock;
        _replies = replies;
        _logger = logger;
    }

    /// <summary>One SafetyEvaluation per event, in order (deterministic code, never the model). The
    /// events of source message messageDbId already in the store are not used as combination context.</summary>
    public async Task<IReadOnlyList<SafetyEvaluation>> EvaluateAsync(
        long familyId, HealthProfileInfo profile, IReadOnlyList<NewHealthEvent> events, long? messageDbId, CancellationToken cancellationToken)
    {
        var rules = await _profiles.GetRulesAsync(familyId, profile.Id, cancellationToken);
        var recent = await LoadComboContextAsync(familyId, profile, events, rules, messageDbId, cancellationToken);
        return SafetyRuleEvaluator.Evaluate(events, recent, rules, _clock.UtcNow);
    }

    // Earlier active events the combination rule may pair with (deleted ones never count). Read before
    // the new events are saved, so a new event never pairs with itself through the store; the
    // message's own earlier events are left out too (an edit replaces them).
    private async Task<IReadOnlyList<HealthEventInfo>> LoadComboContextAsync(
        long familyId, HealthProfileInfo profile, IReadOnlyList<NewHealthEvent> events, IReadOnlyList<SafetyRuleInfo> rules,
        long? messageDbId, CancellationToken cancellationToken)
    {
        var combo = rules.FirstOrDefault(r => r.RuleKey == SafetyRuleKeys.ComboBpSymptoms);
        if (combo?.WindowHours is not { } hours || hours <= 0)
        {
            return Array.Empty<HealthEventInfo>();
        }

        var window = TimeSpan.FromHours(hours);
        var from = events.Min(e => e.OccurredAt) - window;
        // GetActiveAsync's upper bound is exclusive; the evaluator checks the exact window.
        var to = events.Max(e => e.OccurredAt) + window + TimeSpan.FromSeconds(1);
        var active = await _events.GetActiveAsync(familyId, profile.Id, from, to, cancellationToken);
        return active.Where(e => messageDbId is null || e.SourceMessageId != messageDbId).ToList();
    }

    // One fixed alert per saved event that reached an alert level, in event order, as a reply to the
    // message the events were read from: claimed in safety_alerts first and sent only when this call
    // won the claim, so a redelivery never alerts twice. A rule in alreadySent was sent while asking
    // Да/Нет: it is claimed but not sent again. A failed claim still sends (a duplicate alert is better
    // than a missing one). A failed send is retried once in place; if that fails too it is logged as
    // an Error (rule key and message id only) and the claim row stays, so a redelivery does not send it
    // either. Logs rule key and level only, never values. Returns whether the rules decided at least
    // one alert for these events, whether or not it was claimed or delivered.
    public async Task<bool> SendAlertsAsync(
        ITelegramClient telegramClient, long chatId, int? topicId, int replyToMessageId, long familyId, HealthProfileInfo profile,
        IReadOnlyList<HealthEventInfo> saved, IReadOnlyList<SafetyEvaluation> evaluations, long? messageDbId,
        CancellationToken cancellationToken, IReadOnlyCollection<string>? alreadySent = null)
    {
        var anyDecided = evaluations.Any(e => e.Alert is not null);
        for (var i = 0; i < saved.Count && i < evaluations.Count; i++)
        {
            if (evaluations[i].Alert is not { } decision)
            {
                continue;
            }

            var eventId = saved[i].Id;
            var alert = new NewSafetyAlert(
                eventId, decision.RuleKey, decision.Level, decision.Threshold, decision.ThresholdSource, chatId, topicId);
            bool claimed;
            try
            {
                claimed = await _safetyAlerts.TryClaimAsync(familyId, alert, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("safety alert claim failed, sending anyway: {ExceptionType}", ex.GetType().Name);
                claimed = true;
            }

            if (!claimed)
            {
                _logger.LogInformation("Safety alert {RuleKey} for event {EventId} not claimed", decision.RuleKey, eventId);
                continue;
            }

            if (alreadySent is not null && alreadySent.Contains(decision.RuleKey))
            {
                _logger.LogInformation("Safety alert {RuleKey} for event {EventId} was already sent while asking", decision.RuleKey, eventId);
                continue;
            }

            var alertText = SafetyAlertText.Format(decision, profile.EmergencyPhone);
            if (!await _replies.SendAsync(telegramClient, chatId, topicId, alertText, replyToMessageId, cancellationToken)
                && !await _replies.SendAsync(telegramClient, chatId, topicId, alertText, replyToMessageId, cancellationToken))
            {
                _logger.LogError("Safety alert {RuleKey} for message {MessageDbId} could not be sent", decision.RuleKey, messageDbId);
                continue;
            }

            _logger.LogWarning("Safety alert {RuleKey} ({Level}) for event {EventId}", decision.RuleKey, decision.Level, eventId);
        }

        return anyDecided;
    }
}
