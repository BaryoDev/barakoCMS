using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// Which <c>Oidc:Providers</c> sections become a provider, and what one that does not is told (#786).
/// </summary>
public class OidcProvidersTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    private static (string, string?)[] Provider(string name, string authority = "https://idp.example", (string, string?)[]? more = null) =>
    [
        ($"Oidc:Providers:{name}:Authority", authority),
        ($"Oidc:Providers:{name}:ClientId", "client"),
        ($"Oidc:Providers:{name}:ClientSecret", "secret"),
        .. (more ?? Array.Empty<(string, string?)>()).Select(m => ($"Oidc:Providers:{name}:{m.Item1}", m.Item2)),
    ];

    [Fact]
    public void A_section_with_an_authority_a_client_id_and_a_secret_is_a_provider_with_the_defaults()
    {
        var providers = OidcProviders.Enabled(Config(Provider("Keycloak")));

        providers.Should().HaveCount(1);
        var provider = providers[0];
        provider.Name.Should().Be("keycloak", "the name is the route segment, and is lowercased once here");
        provider.DisplayName.Should().Be("keycloak");
        provider.Issuer.Should().Be("https://idp.example", "the issuer is the authority unless it is set");
        provider.Scopes.Should().Be("openid email profile");
        provider.EmailVerifiedClaim.Should().Be("email_verified");
        provider.DiscoveryUrl.Should().Be("https://idp.example/.well-known/openid-configuration");
        OidcProviders.Find(Config(Provider("Keycloak")), "KEYCLOAK").Should().NotBeNull("lookup lowercases the same way");
    }

    [Fact]
    public void With_nothing_configured_there_is_no_provider_and_nothing_to_report()
    {
        OidcProviders.Enabled(Config()).Should().BeEmpty();
        OidcProviders.Problems(Config()).Should().BeEmpty();
        OidcProviders.Find(Config(), "anything").Should().BeNull();
    }

    [Fact]
    public void Scopes_always_include_openid()
    {
        var providers = OidcProviders.Enabled(Config(Provider("a", more: [("Scopes", "email  groups")])));

        providers.Should().HaveCount(1);
        providers[0].Scopes.Should().Be("openid email groups");
    }

    [Theory]
    [InlineData("http://idp.example")]
    [InlineData("idp.example")]
    [InlineData("https://idp.example?tenant=a")]
    [InlineData("https://user:pass@idp.example")]
    [InlineData("")]
    public void An_authority_that_is_not_a_plain_https_address_leaves_the_provider_off_and_says_so(string authority)
    {
        var config = Config(Provider("a", authority));

        OidcProviders.Enabled(config).Should().BeEmpty();
        var problems = OidcProviders.Problems(config);
        problems.Should().HaveCount(1);
        problems[0].Should().Contain("Oidc:Providers:a:Authority");
        problems[0].Should().NotContain("secret").And.NotContain("pass");
    }

    [Fact]
    public void A_provider_with_no_secret_or_an_unusable_name_is_off_and_reported_without_its_values()
    {
        var config = Config(
            ("Oidc:Providers:nosecret:Authority", "https://idp.example"),
            ("Oidc:Providers:nosecret:ClientId", "client"),
            ("Oidc:Providers:bad name!:Authority", "https://idp.example"),
            ("Oidc:Providers:bad name!:ClientId", "client"),
            ("Oidc:Providers:bad name!:ClientSecret", "hunter2"));

        OidcProviders.Enabled(config).Should().BeEmpty();
        var problems = OidcProviders.Problems(config);
        problems.Should().HaveCount(2);
        problems.Should().OnlyContain(p => !p.Contains("hunter2") && !p.Contains("bad name!"));
    }

    [Fact]
    public void A_provider_switched_off_or_under_the_master_switch_is_off_and_not_a_problem()
    {
        var dark = Config(Provider("a", more: [("Enabled", "false")]));
        OidcProviders.Enabled(dark).Should().BeEmpty();
        OidcProviders.Problems(dark).Should().BeEmpty();

        var master = Config([.. Provider("a"), ("ExternalAuth:Enabled", "false")]);
        OidcProviders.Enabled(master).Should().BeEmpty();
        OidcProviders.Find(master, "a").Should().BeNull();
    }

    [Fact]
    public void An_issuer_may_be_a_template_and_must_still_be_https()
    {
        var template = OidcProviders.Enabled(Config(Provider("ms", "https://login.example/common/v2.0",
            [("Issuer", "https://login.example/{tenantid}/v2.0")])));
        template.Should().HaveCount(1);
        template[0].IssuerIsTemplate.Should().BeTrue();
        template[0].Issuer.Should().Be("https://login.example/{tenantid}/v2.0");

        OidcProviders.Enabled(Config(Provider("ms", more: [("Issuer", "http://login.example/{tenantid}/v2.0")])))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_list_is_bounded_and_in_name_order()
    {
        var settings = Enumerable.Range(0, OidcProviders.MaxProviders + 5)
            .SelectMany(i => Provider($"p{i:00}"))
            .ToArray();

        var providers = OidcProviders.Enabled(Config(settings));

        providers.Should().HaveCount(OidcProviders.MaxProviders);
        providers.Select(p => p.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        providers[0].Name.Should().Be("p00");
    }

    [Fact]
    public void A_provider_printed_by_accident_does_not_print_its_secret()
    {
        var provider = OidcProviders.Enabled(Config(Provider("a")))[0];

        provider.ToString().Should().NotContain("secret");
        $"{provider}".Should().Contain("a");
    }
}

/// <summary>A state is spent by its first callback (#786).</summary>
public class OidcConsumedStatesTests
{
    [Fact]
    public void A_state_is_consumed_once_and_refused_while_it_is_remembered()
    {
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var states = new OidcConsumedStates { Now = () => now };

        states.TryConsume("state-a").Should().BeTrue();
        states.TryConsume("state-a").Should().BeFalse();
        states.TryConsume("state-b").Should().BeTrue("another state is another flow");

        now += OidcConsumedStates.Lifetime + TimeSpan.FromSeconds(1);
        states.TryConsume("state-a").Should().BeTrue(
            "past the cookie's lifetime the browser no longer holds it, so the entry is free to go");
    }

    [Fact]
    public void The_set_is_bounded_and_drops_expired_entries_first()
    {
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var states = new OidcConsumedStates { Now = () => now };

        for (var i = 0; i < OidcConsumedStates.Capacity; i++)
        {
            states.TryConsume($"old-{i}").Should().BeTrue();
        }

        states.Count.Should().Be(OidcConsumedStates.Capacity);

        now += OidcConsumedStates.Lifetime + TimeSpan.FromSeconds(1);
        states.TryConsume("fresh").Should().BeTrue();
        states.Count.Should().Be(1, "every earlier entry had expired, so only the new one is left");

        for (var i = 0; i < OidcConsumedStates.Capacity + 10; i++)
        {
            states.TryConsume($"live-{i}").Should().BeTrue();
        }

        states.Count.Should().BeLessThanOrEqualTo(OidcConsumedStates.Capacity, "live entries cannot grow it past the bound either");
    }
}
