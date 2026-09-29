using System.Globalization;
using Assistant.Application.Manager;

namespace Assistant.Infrastructure.Manager;

public class ClaimCodeProvider : IClaimCodeProvider
{
    public string Code { get; } = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
}
