using System.Security.Cryptography;
using System.Text;

namespace Assistant.IntegrationTests.Infrastructure;

public static class TemplateHash
{
    public static string Compute(IEnumerable<string> migrationIds)
    {
        var joined = string.Join("|", migrationIds);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
