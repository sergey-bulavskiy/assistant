using System.Net;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ProxyHandlerFactoryTests
{
    [Fact]
    public void Empty_url_returns_an_ok_result_with_a_null_handler_meaning_use_the_default_handler()
    {
        var result = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", "");
        result.IsFailed.ShouldBeFalse();
        result.Handler.ShouldBeNull();

        var whitespaceResult = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", "   ");
        whitespaceResult.IsFailed.ShouldBeFalse();
        whitespaceResult.Handler.ShouldBeNull();
    }

    [Fact]
    public void A_plain_http_proxy_with_no_credentials_is_configured_without_credentials()
    {
        var result = ProxyHandlerFactory.Create("OPENAI_PROXY", "http://proxy.example:8080");

        result.IsFailed.ShouldBeFalse();
        using var handler = result.Handler;
        handler.ShouldNotBeNull();
        handler!.UseProxy.ShouldBeTrue();
        var proxy = (WebProxy)handler.Proxy!;
        proxy.Address!.ToString().ShouldBe("http://proxy.example:8080/");
        proxy.Credentials.ShouldBeNull();
    }

    [Fact]
    public void An_https_proxy_is_accepted()
    {
        var result = ProxyHandlerFactory.Create("OPENAI_PROXY", "https://proxy.example:8443");

        result.IsFailed.ShouldBeFalse();
        using var handler = result.Handler;
        handler.ShouldNotBeNull();
        var proxy = (WebProxy)handler!.Proxy!;
        proxy.Address!.Scheme.ShouldBe("https");
    }

    [Fact]
    public void A_socks5_proxy_with_userinfo_splits_it_into_NetworkCredential()
    {
        var result = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", "socks5://proxyuser:proxypass@proxy.example:1080");

        using var handler = result.Handler;
        var proxy = (WebProxy)handler!.Proxy!;
        proxy.Address!.Scheme.ShouldBe("socks5");
        var credential = (NetworkCredential)proxy.Credentials!;
        credential.UserName.ShouldBe("proxyuser");
        credential.Password.ShouldBe("proxypass");
    }

    [Fact]
    public void A_percent_encoded_password_is_unescaped()
    {
        var result = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", "http://proxyuser:p%40ss@proxy.example:8080");

        using var handler = result.Handler;
        var proxy = (WebProxy)handler!.Proxy!;
        var credential = (NetworkCredential)proxy.Credentials!;
        credential.UserName.ShouldBe("proxyuser");
        credential.Password.ShouldBe("p@ss");
    }

    [Fact]
    public void Userinfo_is_stripped_from_the_proxy_address_itself()
    {
        var result = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", "http://proxyuser:proxypass@proxy.example:8080");

        using var handler = result.Handler;
        var proxy = (WebProxy)handler!.Proxy!;
        proxy.Address!.UserInfo.ShouldBeEmpty();
    }

    [Fact]
    public void An_invalid_url_fails_naming_only_the_variable_never_the_url()
    {
        const string secretUrl = "http://proxyuser:topsecret@proxy.example:8080 not a url";

        var result = ProxyHandlerFactory.Create("ANTHROPIC_PROXY", secretUrl);

        result.IsFailed.ShouldBeTrue();
        result.Handler.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("ANTHROPIC_PROXY");
        result.Error!.ShouldNotContain("topsecret");
        result.Error!.ShouldNotContain("proxy.example");
    }

    [Fact]
    public void A_relative_url_is_rejected()
    {
        var result = ProxyHandlerFactory.Create("OPENAI_PROXY", "/not-absolute");

        result.IsFailed.ShouldBeTrue();
        result.Error!.ShouldContain("OPENAI_PROXY");
    }

    [Theory]
    [InlineData("ftp://proxy.example:21")]
    [InlineData("ssh://proxy.example:22")]
    public void An_unsupported_scheme_is_rejected(string url)
    {
        var result = ProxyHandlerFactory.Create("OPENAI_PROXY", url);

        result.IsFailed.ShouldBeTrue();
        result.Handler.ShouldBeNull();
        result.Error!.ShouldContain("OPENAI_PROXY");
        result.Error!.ShouldNotContain(url);
    }

    [Fact]
    public void A_configured_handler_uses_SocketsHttpHandler_with_a_15_minute_pooled_connection_lifetime()
    {
        var result = ProxyHandlerFactory.Create("OPENAI_PROXY", "http://proxy.example:8080");

        using var handler = result.Handler;
        handler.ShouldNotBeNull();
        handler!.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(15));
    }
}
