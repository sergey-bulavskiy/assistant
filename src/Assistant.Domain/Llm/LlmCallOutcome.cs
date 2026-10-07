namespace Assistant.Domain.Llm;

public enum LlmCallOutcome
{
    Ok,
    LimitReached,
    Failed,
    Timeout,
    Dispatching,
    OutcomeUnknown
}
