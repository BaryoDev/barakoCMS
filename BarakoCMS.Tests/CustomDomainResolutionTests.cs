using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Two sites on their own domains, as separate tenants on one deployment, is the central
/// multi-tenant case.
/// </summary>
/// <remarks>
/// Before this, <c>ResolveSlug</c> returned null for any host with fewer than three labels, so
/// <c>abc.com</c> and <c>xyz.com</c> both fell through to the default tenant and were served the same
/// content. Nothing errored. A misconfigured deployment showed the wrong client's site, which is a
/// failure a client notices rather than CI.
/// </remarks>
public class CustomDomainResolutionTests
{
    private static TenantDomainMap Map(params (string Domain, string Slug)[] entries) =>
        new(entries.Select(e => (e.Domain, e.Slug)));

    [Fact]
    public void A_custom_domain_resolves_to_its_own_tenant()
    {
        var map = Map(("abc.com", "abc"), ("xyz.com", "xyz"));

        TenantResolutionMiddleware.Resolve("abc.com", map).Slug.Should().Be("abc");
        TenantResolutionMiddleware.Resolve("xyz.com", map).Slug.Should().Be("xyz");
    }

    /// <summary>
    /// The bug this file exists for. Asserting only that abc.com resolves to "abc" would pass against
    /// a resolver that returns "abc" for everything, so both domains are checked against each other.
    /// </summary>
    [Fact]
    public void Two_custom_domains_do_not_collapse_onto_one_tenant()
    {
        var map = Map(("abc.com", "abc"), ("xyz.com", "xyz"));

        var abc = TenantResolutionMiddleware.Resolve("abc.com", map);
        var xyz = TenantResolutionMiddleware.Resolve("xyz.com", map);

        abc.Slug.Should().NotBe(xyz.Slug,
            "serving two client domains the same tenant's content is the defect being fixed");
    }

    [Theory]
    [InlineData("www.abc.com")]
    [InlineData("WWW.ABC.COM")]
    [InlineData("abc.com.")]     // trailing dot, a fully qualified host
    public void A_domain_matches_regardless_of_www_or_case_or_trailing_dot(string host)
    {
        var map = Map(("abc.com", "abc"));

        TenantResolutionMiddleware.Resolve(host, map).Slug.Should().Be("abc",
            "www.abc.com and abc.com are the same site, and silently differing is how one of them "
            + "ends up on the default tenant");
    }

    /// <summary>
    /// The positive control for the www rule. Without it, a resolver that strips every leading label
    /// would pass the test above and also break subdomain tenants.
    /// </summary>
    [Fact]
    public void Stripping_www_does_not_strip_a_real_subdomain_tenant()
    {
        TenantResolutionMiddleware.Resolve("acme.example.com", TenantDomainMap.Empty)
            .Slug.Should().Be("acme");
    }

    [Fact]
    public void An_unknown_host_falls_back_to_the_default_when_strict_is_off()
    {
        var result = TenantResolutionMiddleware.Resolve("unknown.com", TenantDomainMap.Empty);

        result.Slug.Should().BeNull("a null slug leaves TenantContext on its default");
        result.Unrecognised.Should().BeTrue();
    }

    /// <summary>
    /// Falling back to the default tenant is what produced the silent wrong-content failure, so the
    /// caller is told the host was not recognised and can refuse instead. The switch exists so
    /// existing single-tenant deployments are unaffected.
    /// </summary>
    [Fact]
    public void An_unknown_host_is_reported_as_unrecognised_so_strict_mode_can_refuse()
    {
        var map = Map(("abc.com", "abc"));

        TenantResolutionMiddleware.Resolve("somewhere-else.com", map)
            .Unrecognised.Should().BeTrue();
    }

    [Theory]
    [InlineData("abc.com")]
    [InlineData("acme.example.com")]
    public void A_resolved_host_is_not_flagged_unrecognised(string host)
    {
        var map = Map(("abc.com", "abc"));

        TenantResolutionMiddleware.Resolve(host, map).Unrecognised.Should().BeFalse();
    }

    /// <summary>
    /// Infra labels stay reserved. Without this, admin.example.com would resolve to a tenant named
    /// "admin" and the admin UI would look like a customer site.
    /// </summary>
    [Theory]
    [InlineData("www.example.com")]
    [InlineData("api.example.com")]
    [InlineData("admin.example.com")]
    [InlineData("app.example.com")]
    public void Infra_subdomains_still_resolve_to_the_default(string host)
    {
        TenantResolutionMiddleware.Resolve(host, TenantDomainMap.Empty).Slug.Should().BeNull();
    }

    /// <summary>
    /// A custom domain listed for a tenant wins over the subdomain rule, so a tenant can be reached
    /// at admin.theirbrand.com if they actually own it.
    /// </summary>
    [Fact]
    public void An_explicit_domain_beats_the_infra_label_rule()
    {
        var map = Map(("admin.theirbrand.com", "theirs"));

        TenantResolutionMiddleware.Resolve("admin.theirbrand.com", map).Slug.Should().Be("theirs");
    }

    [Fact]
    public void A_domain_registered_twice_is_rejected_rather_than_picking_a_winner()
    {
        var build = () => Map(("abc.com", "abc"), ("ABC.com", "other"));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*abc.com*",
                "an ambiguous domain resolves arbitrarily, which is the same silent-wrong-answer "
                + "shape as the original bug");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void Hosts_that_cannot_carry_a_tenant_resolve_to_the_default(string? host)
    {
        TenantResolutionMiddleware.Resolve(host, TenantDomainMap.Empty).Slug.Should().BeNull();
    }
}
