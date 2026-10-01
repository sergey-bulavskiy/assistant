using System.Net;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ProxyHandlerFactoryTests
{
    [Fact]
    public void Empty_url_returns_null_meaning_use_the_default_handler()
    {
        ProxyHandlerFactory.Create("").ShouldBeNull();
        ProxyHandlerFactory.Create("   ").ShouldBeNull();
    }

    [Fact]
    public void A_plain_http_proxy_with_no_credentials_is_configured_without_credentials()
    {
        using var handler = ProxyHandlerFactory.Create("http://proxy.example:8080");

        handler.ShouldNotBeNull();
        handler!.UseProxy.ShouldBeTrue();
        var proxy = (WebProxy)handler.Proxy!;
        proxy.Address!.ToString().ShouldBe("http://proxy.example:8080/");
        proxy.Credentials.ShouldBeNull();
    }

    [Fact]
    public void A_socks5_proxy_with_userinfo_splits_it_into_NetworkCredential()
    {
        using var handler = ProxyHandlerFactory.Create("socks5://proxyuser:proxypass@proxy.example:1080");

        var proxy = (WebProxy)handler!.Proxy!;
        proxy.Address!.Scheme.ShouldBe("socks5");
        var credential = (NetworkCredential)proxy.Credentials!;
        credential.UserName.ShouldBe("proxyuser");
        credential.Password.ShouldBe("proxypass");
    }

    [Fact]
    public void Userinfo_is_stripped_from_the_proxy_address_itself()
    {
        using var handler = ProxyHandlerFactory.Create("http://proxyuser:proxypass@proxy.example:8080");

        var proxy = (WebProxy)handler!.Proxy!;
        proxy.Address!.UserInfo.ShouldBeEmpty();
    }
}
