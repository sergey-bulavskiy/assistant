namespace Assistant.Infrastructure.Llm;

/// <summary>Registered instead of BudgetGuard when LLM is off/misconfigured (no LlmConfig to read a
/// Budget from), so IBudgetGuard is always resolvable -- UsageCommandHandler takes a plain
/// IBudgetGuard and never special-cases "LLM off" itself; EvaluateAsync returning null here means
/// exactly the same thing it means from a real BudgetGuard with LlmConfig.Budget null: "no budget
/// configured".</summary>
public class NullBudgetGuard : IBudgetGuard
{
    public Task<BudgetStatus?> EvaluateAsync(CancellationToken cancellationToken) => Task.FromResult<BudgetStatus?>(null);
}
