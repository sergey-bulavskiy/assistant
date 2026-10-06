using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Assistant.Infrastructure.Diagnostics;

public sealed class TraceSecretRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    public TraceSecretRegistry(IConfiguration configuration)
    {
        foreach (var name in new[]
        {
            "TELEGRAM_MANAGER_BOT_TOKEN", "TOKEN_ENCRYPTION_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
            "ANTHROPIC_API_KEY", "OPENAI_API_KEY"
        }) Add(configuration[name]);

        var connectionString = configuration.GetConnectionString("Assistant");
        Add(connectionString);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try { Add(new NpgsqlConnectionStringBuilder(connectionString).Password); }
            catch (Exception) { /* Invalid configuration must not prevent normal startup. */ }
        }

        foreach (var name in new[] { "ANTHROPIC_PROXY", "OPENAI_PROXY" })
        {
            var proxy = configuration[name];
            Add(proxy);
            if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                foreach (var part in uri.UserInfo.Split(':', 2))
                {
                    try { Add(Uri.UnescapeDataString(part)); }
                    catch (Exception) { /* Keep the full configured URL registered above. */ }
                }
            }
        }
    }

    public void Add(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        lock (_gate) _values.Add(value);
    }

    public string Redact(string value)
    {
        string[] secrets;
        lock (_gate) secrets = _values.OrderByDescending(x => x.Length).ToArray();
        foreach (var secret in secrets) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
}
