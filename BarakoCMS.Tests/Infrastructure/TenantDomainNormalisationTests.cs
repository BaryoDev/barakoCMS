using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>How a typed domain becomes the stored host (#1280).</summary>
public class TenantDomainNormalisationTests
{
    [Fact]
    public void A_name_with_non_ASCII_letters_is_stored_in_its_ASCII_form()
    {
        var stored = TenantDomains.Normalise(["Bücher.Example"], "edge.example.net", out var errors);

        errors.Should().BeEmpty();
        stored.Should().Equal("xn--bcher-kva.example");
    }

    [Fact]
    public void A_name_that_cannot_be_encoded_is_refused()
    {
        TenantDomains.Normalise(["-.example", "a\u0000b.example"], null, out var errors);

        errors.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("edge.example.net")]
    [InlineData("EDGE.example.net.")]
    [InlineData("www.edge.example.net")]
    [InlineData("tenant.edge.example.net")]
    [InlineData("a.b.edge.example.net")]
    public void The_deployments_host_and_anything_under_it_is_refused(string typed)
    {
        var stored = TenantDomains.Normalise([typed], "edge.example.net", out var errors);

        stored.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Contain("own host");
    }

    [Theory]
    [InlineData("notedge.example.net")]
    [InlineData("edge.example.net.evil.test")]
    [InlineData("example.net")]
    public void A_name_that_only_looks_like_the_deployments_host_is_kept(string typed)
    {
        TenantDomains.Normalise([typed], "edge.example.net", out var errors).Should().Equal(typed);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void The_check_is_off_when_no_deployment_host_is_known()
    {
        TenantDomains.Normalise(["edge.example.net"], null, out var errors).Should().Equal("edge.example.net");
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("*.shop.example")]
    [InlineData("203.0.113.9")]
    [InlineData("shop.example:8443")]
    [InlineData("localhost")]
    public void Wildcards_addresses_and_ports_are_still_refused(string typed)
    {
        TenantDomains.Normalise([typed], "edge.example.net", out var errors).Should().BeEmpty();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_www_name_and_the_bare_name_are_one_domain()
    {
        TenantDomains.Normalise(["www.shop.example", "shop.example"], null, out _).Should().Equal("shop.example");
    }

    [Fact]
    public void The_deployment_host_is_the_setting_else_the_host_of_the_base_url()
    {
        static IConfiguration Config(params (string, string)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

        TenantDomains.DeploymentHost(Config(("Tenancy:DeploymentHost", "Edge.Example.Net"), ("App:BaseUrl", "https://api.example.com")))
            .Should().Be("edge.example.net");
        TenantDomains.DeploymentHost(Config(("App:BaseUrl", "https://api.example.com:8443/x"))).Should().Be("api.example.com");
        TenantDomains.DeploymentHost(Config()).Should().BeNull();
    }
}
