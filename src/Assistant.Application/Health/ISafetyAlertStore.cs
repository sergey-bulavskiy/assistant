namespace Assistant.Application.Health;

/// <summary>One alert to record before it is sent. EventId is events.id; Level a SafetyAlertLevels
/// value; ThresholdSource a SafetyRuleSources value; Threshold null for symptom and combination
/// alerts; ChatId/TopicId where the alert goes (where the reading was posted).</summary>
public sealed record NewSafetyAlert(
    long EventId, string RuleKey, string Level, decimal? Threshold, string ThresholdSource, long ChatId, int? TopicId);

/// <summary>safety_alerts: at most one alert per event and rule. Fails closed like the other health
/// stores: throws InvalidOperationException unless ICurrentFamily.FamilyId equals familyId. Runs on
/// the request's DI scope only.</summary>
public interface ISafetyAlertStore
{
    /// <summary>Inserts the row unless one exists for (EventId, RuleKey). True only when this call
    /// inserted it: the caller sends the alert only then. Also false when the event is not an active
    /// event of this family. A duplicate is never an exception and nothing is left tracked.</summary>
    Task<bool> TryClaimAsync(long familyId, NewSafetyAlert alert, CancellationToken cancellationToken);
}
