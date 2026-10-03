using Assistant.Application.Health;

namespace Assistant.UnitTests.Fakes;

public class FakeSafetyAlertStore : ISafetyAlertStore
{
    public List<(long FamilyId, NewSafetyAlert Alert)> Claims { get; } = new();

    /// <summary>False simulates a row that already exists.</summary>
    public bool ClaimResult { get; set; } = true;

    /// <summary>When true, TryClaimAsync records the call and then throws.</summary>
    public bool ThrowOnClaim { get; set; }

    /// <summary>Called with each claim before it returns; lets a test see what was already sent.</summary>
    public Action? OnClaim { get; set; }

    public Task<bool> TryClaimAsync(long familyId, NewSafetyAlert alert, CancellationToken cancellationToken)
    {
        Claims.Add((familyId, alert));
        OnClaim?.Invoke();
        if (ThrowOnClaim)
        {
            throw new InvalidOperationException("simulated claim failure");
        }

        return Task.FromResult(ClaimResult);
    }
}
