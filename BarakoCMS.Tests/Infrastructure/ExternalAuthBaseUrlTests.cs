using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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

    private static HttpContext Context(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        return context;
    }

    [Fact]
    public void A_forged_host_does_not_become_the_redirect_uri()
    {
        var act = () => ExternalAuthSupport.BaseUrl(
            Config(("AllowedHosts", "*")), Context("attacker.example.net"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*App:BaseUrl*",
                "refusing names the setting; building the URL from the header would hand the caller the origin");
    }

    [Fact]
    public void The_configured_base_url_is_used_even_when_the_host_is_forged()
    {
        ExternalAuthSupport.BaseUrl(
                Config(("AllowedHosts", "*"), ("App:BaseUrl", "https://cms.example.com/")),
                Context("attacker.example.net"))
            .Should().Be("https://cms.example.com");
    }

    /// <summary>
    /// The positive control. A resolver that threw on every request would pass the test above and
    /// take every OAuth flow in the product down with it.
    /// </summary>
    [Fact]
    public void A_deployment_that_names_its_hosts_still_works_with_no_base_url_set()
    {
        ExternalAuthSupport.BaseUrl(
                Config(("AllowedHosts", "cms.example.com")), Context("cms.example.com"))
            .Should().Be("https://cms.example.com");
    }
}
