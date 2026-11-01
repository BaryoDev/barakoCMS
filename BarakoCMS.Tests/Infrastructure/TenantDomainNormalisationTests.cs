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

    [Fact]
    public void A_www_deployment_host_keeps_its_www_as_the_cname_target()
    {
        static IConfiguration Config(string key, string value) =>
            new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]).Build();

        TenantDomains.DeploymentHost(Config("Tenancy:DeploymentHost", " WWW.Example.com. ")).Should().Be("www.example.com");
        TenantDomains.DeploymentHost(Config("App:BaseUrl", "https://www.example.com/app")).Should().Be("www.example.com");
    }

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("example.com")]
    [InlineData("shop.example.com")]
    [InlineData("shop.www.example.com")]
    public void With_a_www_deployment_host_the_host_and_the_bare_name_under_it_are_both_refused(string typed)
    {
        TenantDomains.Normalise([typed], "www.example.com", out var errors).Should().BeEmpty();
        errors.Should().ContainSingle();
    }

    [Fact]
    public void A_claim_is_capped_so_the_challenge_name_still_fits_a_DNS_name()
    {
        var fits = Host(TenantDomains.MaxHostLength);
        var tooLong = Host(TenantDomains.MaxHostLength + 1);

        fits.Length.Should().Be(TenantDomains.MaxHostLength);
        TenantDomains.Normalise([fits], null, out _).Should().NotBeEmpty();
        TenantDomains.Normalise([tooLong], null, out var errors).Should().BeEmpty();
        errors.Should().ContainSingle();
        ("_barako-challenge." + fits).Length.Should().BeLessThanOrEqualTo(253);
    }

    // Labels of 50 letters, then a last label sized to land on the length, then "com".
    private static string Host(int length)
    {
        var body = length - 3;
        var groups = body / 51;
        var rest = body - groups * 51;
        if (rest == 1)
        {
            groups--;
            rest += 51;
        }

        return string.Concat(Enumerable.Repeat(new string('a', 50) + ".", groups))
            + (rest > 0 ? new string('a', rest - 1) + "." : string.Empty) + "com";
    }

    [Fact]
    public void A_stored_host_over_the_claim_cap_is_still_a_domain_shape_but_a_new_claim_of_it_is_refused()
    {
        var stored = Host(TenantDomains.MaxHostLength + 10);

        TenantDomains.Normalise([stored], out var shapeErrors).Should().ContainSingle("the shape check allows what DNS allows, so a lookup still recognises a stored host");
        shapeErrors.Should().BeEmpty();
        TenantDomains.Normalise([stored], null, out var claimErrors).Should().BeEmpty("a new claim has to leave room for the challenge label");
        claimErrors.Should().ContainSingle();
    }

    [Fact]
    public void Saving_a_tenant_that_already_lists_a_long_host_keeps_it_and_still_refuses_a_new_one()
    {
        var stored = Host(TenantDomains.MaxHostLength + 10);
        var another = "b" + Host(TenantDomains.MaxHostLength + 12)[1..];

        TenantDomains.Normalise([stored, "shop.example.com"], null, [stored], out var kept).Should().Contain(stored);
        kept.Should().BeEmpty("a host the tenant already lists is not re-judged against the claim cap");

        TenantDomains.Normalise([stored, another], null, [stored], out var added).Should().Equal(stored);
        added.Should().ContainSingle("a long host that is not already listed is a new claim");
    }
}
