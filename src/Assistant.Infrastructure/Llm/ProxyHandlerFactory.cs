using System.Net;

namespace Assistant.Infrastructure.Llm;

/// <summary>Builds an HttpClientHandler configured with a per-provider proxy (ANTHROPIC_PROXY /
/// OPENAI_PROXY), or null when no proxy is configured (meaning: use the default handler). Supports
/// http://, https:// and socks5:// (native since .NET 6 -- Verified facts §C). WebProxy does NOT
/// parse userinfo out of the URL itself (confirmed open dotnet/runtime issue #125341) -- this class
/// splits user:pass@ manually and sets WebProxy.Credentials explicitly. The proxy URL is a secret
/// (it may carry credentials): never log it, not even in an exception message.</summary>
public static class ProxyHandlerFactory
{
    public static HttpClientHandler? Create(string proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return null;
        }

        var uri = new Uri(proxyUrl);
        var proxy = new WebProxy(StripUserInfo(uri));

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(parts[0]);
            var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            proxy.Credentials = new NetworkCredential(user, password);
        }

        return new HttpClientHandler
        {
            UseProxy = true,
            Proxy = proxy
        };
    }

    private static Uri StripUserInfo(Uri uri)
    {
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri;
    }
}
