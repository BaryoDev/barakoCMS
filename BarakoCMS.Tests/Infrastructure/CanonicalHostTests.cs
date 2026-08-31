using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The rule that keeps a caller-supplied Host header out of the URLs this application hands to
/// somebody else (#147).
/// </summary>
/// <remarks>
/// Every refusal case here is paired with a case that must still succeed. A resolver that returned
/// null for everything would satisfy the injection tests on its own and break every deployment, so
/// the positive controls are the half that says the fix is usable.
/// </remarks>
public class CanonicalHostTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    private static HttpRequest Request(string host, string scheme = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("cms.example.com;*")]
    public void An_allowed_hosts_value_that_accepts_everything_does_not_constrain_the_host(string? allowedHosts)
    {
        CanonicalHost.IsHostFilteringConstrained(allowedHosts).Should().BeFalse();
    }

    [Theory]
    [InlineData("cms.example.com")]
    [InlineData("cms.example.com;www.example.com")]
    [InlineData("*.example.com")]
    public void A_named_host_constrains_the_header(string allowedHosts)
    {
        CanonicalHost.IsHostFilteringConstrained(allowedHosts).Should().BeTrue(
            "host filtering has already rejected anything not on this list");
    }

    [Fact]
    public void With_nothing_configured_and_every_host_allowed_there_is_no_base_url()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("AllowedHosts", "*")), Request("attacker.example.net"));

        resolved.Should().BeNull(
            "the Host header is written by the caller, so it must not become the origin of a link");
    }

    [Fact]
    public void The_configured_base_url_wins_over_the_request_host()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("AllowedHosts", "*"), ("App:BaseUrl", "https://cms.example.com/")),
            Request("attacker.example.net"));

        resolved.Should().Be("https://cms.example.com");
    }

    [Fact]
    public void A_feature_specific_setting_is_preferred_over_the_application_base_url()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("App:BaseUrl", "https://cms.example.com"), ("Feeds:SiteUrl", "https://www.example.com")),
            Request("attacker.example.net"),
            "Feeds:SiteUrl");

        resolved.Should().Be("https://www.example.com",
            "the feed links point at the frontend, which is a different host from the API");
    }

    [Fact]
    public void The_application_base_url_is_used_when_the_feature_setting_is_absent()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("App:BaseUrl", "https://cms.example.com")),
            Request("attacker.example.net"),
            "Feeds:SiteUrl");

        resolved.Should().Be("https://cms.example.com");
    }

    /// <summary>
    /// The positive control for the whole design: once AllowedHosts names the hosts, host filtering
    /// has done the checking and the request's own origin is a legitimate answer again.
    /// </summary>
    [Fact]
    public void A_constrained_host_is_used_when_nothing_is_configured()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("AllowedHosts", "cms.example.com")), Request("cms.example.com"));

        resolved.Should().Be("https://cms.example.com");
    }

    [Fact]
    public void The_scheme_comes_from_the_request_not_a_guess()
    {
        var resolved = CanonicalHost.BaseUrl(
            Config(("AllowedHosts", "cms.example.com")), Request("cms.example.com", scheme: "http"));

        resolved.Should().Be("http://cms.example.com");
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("/cms")]
    [InlineData("ftp://example.com")]
    public void A_configured_value_that_is_not_an_absolute_web_url_is_refused(string configured)
    {
        var act = () => CanonicalHost.BaseUrl(Config(("App:BaseUrl", configured)), Request("cms.example.com"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*App:BaseUrl*",
                "a relative value produces links that resolve against whoever fetched the document");
    }

    [Fact]
    public void The_message_names_both_settings_that_would_fix_it()
    {
        var message = CanonicalHost.NotConfigured("Feeds:SiteUrl");

        message.Should().Contain("Feeds:SiteUrl");
        message.Should().Contain("AllowedHosts");
    }
}
