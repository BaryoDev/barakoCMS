using barakoCMS.Infrastructure.Security;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The OAuth base URL, which every provider's redirect_uri is built from (#147).
/// </summary>
/// <remarks>
/// This took the Host header whenever App:BaseUrl was unset, and App:BaseUrl is unset by default, so
/// on a deployment with AllowedHosts "*" the caller chose the host in their own authorization
/// request. The providers reject a redirect_uri they were not given, so this was never known to be
/// exploitable, but the check belonged to Google rather than to us.
/// </remarks>
public class ExternalAuthBaseUrlTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    /// <summary>
    /// A request whose services hold core's own <see cref="IPublicBaseUrl"/> over <paramref name="config"/>,
    /// which is what a host registers and what ExternalAuth asks for.
    /// </summary>
    private static HttpContext Context(IConfiguration config, string host)
    {
        var services = new ServiceCollection()
            .AddSingleton<IPublicBaseUrl>(new CanonicalPublicBaseUrl(config))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        return context;
    }

    [Fact]
    public void A_forged_host_does_not_become_the_redirect_uri()
    {
        var config = Config(("AllowedHosts", "*"));
        var act = () => ExternalAuthSupport.BaseUrl(Context(config, "attacker.example.net"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*App:BaseUrl*",
                "refusing names the setting; building the URL from the header would hand the caller the origin");
    }

    [Fact]
    public void The_configured_base_url_is_used_even_when_the_host_is_forged()
    {
        var config = Config(("AllowedHosts", "*"), ("App:BaseUrl", "https://cms.example.com/"));
        ExternalAuthSupport.BaseUrl(Context(config, "attacker.example.net"))
            .Should().Be("https://cms.example.com");
    }

    /// <summary>
    /// The positive control. A resolver that threw on every request would pass the test above and
    /// take every OAuth flow in the product down with it.
    /// </summary>
    [Fact]
    public void A_deployment_that_names_its_hosts_still_works_with_no_base_url_set()
    {
        var config = Config(("AllowedHosts", "cms.example.com"));
        ExternalAuthSupport.BaseUrl(Context(config, "cms.example.com"))
            .Should().Be("https://cms.example.com");
    }
}
