using System.Net;

namespace Assistant.Infrastructure.Llm;

/// <summary>Builds a SocketsHttpHandler configured with a per-provider proxy (ANTHROPIC_PROXY /
/// OPENAI_PROXY), or a null handler when no proxy is configured (meaning: use the default handler).
/// Supports http://, https:// and socks5:// (native since .NET 6 -- Verified facts §C) and rejects
/// anything else, including a malformed/relative URL. `WebProxy` does NOT parse userinfo out of the
/// URL itself (confirmed open dotnet/runtime issue #125341) -- this class splits user:pass@ manually
/// and sets `WebProxy.Credentials` explicitly. The proxy URL is a secret (it may carry credentials):
/// never log it, not even in an exception message -- review finding S2 found the previous code threw
/// `new Uri(proxyUrl)`'s own `UriFormatException`, which .NET's default formatting embeds the input
/// string into, so a malformed `ANTHROPIC_PROXY`/`OPENAI_PROXY` could leak credentials into a crash
/// log. `Create` never throws: a bad URL comes back as `ProxyHandlerResult.Failed(error)` naming only
/// the variable, never the value, so Task 9 can drop that provider's entries with one `Error` (the
/// same pattern §10.5 amendment 2 uses for other invalid paid entries) instead of crashing.
/// Prefers `SocketsHttpHandler` (`PooledConnectionLifetime` = 15 min, review nit) over
/// `HttpClientHandler`: it is the modern .NET HTTP handler and recycles pooled connections, which
/// `HttpClientHandler` does not expose a way to do.</summary>
public static class ProxyHandlerFactory
{
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] AllowedSchemes = { "http", "https", "socks5" };

    public static ProxyHandlerResult Create(string variableName, string proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return ProxyHandlerResult.Ok(null);
        }

        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) ||
            !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            // Never include proxyUrl (or `uri`) here: it may carry credentials (review finding S2).
            return ProxyHandlerResult.Failed(
                $"{variableName} must be an absolute http://, https:// or socks5:// proxy URL.");
        }

        var proxy = new WebProxy(StripUserInfo(uri));
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(parts[0]);
            var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            proxy.Credentials = new NetworkCredential(user, password);
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = proxy,
            PooledConnectionLifetime = PooledConnectionLifetime
        };
        return ProxyHandlerResult.Ok(handler);
    }

    private static Uri StripUserInfo(Uri uri)
    {
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri;
    }
}

/// <summary>Result of <see cref="ProxyHandlerFactory.Create"/>: exactly one of <see cref="Handler"/>
/// (including a legitimate null meaning "no proxy configured") or <see cref="Error"/> is meaningful --
/// check <see cref="IsFailed"/> first.</summary>
public readonly struct ProxyHandlerResult
{
    public SocketsHttpHandler? Handler { get; }

    public string? Error { get; }

    public bool IsFailed => Error is not null;

    private ProxyHandlerResult(SocketsHttpHandler? handler, string? error)
    {
        Handler = handler;
        Error = error;
    }

    public static ProxyHandlerResult Ok(SocketsHttpHandler? handler) => new(handler, null);

    public static ProxyHandlerResult Failed(string error) => new(null, error);
}
