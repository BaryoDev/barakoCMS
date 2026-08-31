using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The Strict-Transport-Security policy (#130).
/// </summary>
/// <remarks>
/// The defaults matter more here than in any other header, because this is the only one a browser
/// keeps after the response is gone. An over-long max-age with includeSubDomains cannot be undone by
/// deploying again, so these cases pin the cautious defaults rather than only checking that a value
/// arrives.
/// </remarks>
public class HstsPolicyTests
{
    private static HstsOptions Configure(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

        var options = new HstsOptions();
        HstsPolicy.Configure(options, configuration);
        return options;
    }

    [Fact]
    public void The_default_max_age_is_ninety_days()
    {
        Configure().MaxAge.Should().Be(TimeSpan.FromDays(90),
            "a mistake has to age out within a quarter, not a year");
    }

    [Fact]
    public void Subdomains_are_not_included_by_default()
    {
        Configure().IncludeSubDomains.Should().BeFalse(
            "includeSubDomains covers subdomains that are not on HTTPS yet, and cannot be recalled");
    }

    [Fact]
    public void Preload_is_never_set()
    {
        Configure(("Hsts:MaxAgeDays", "365"), ("Hsts:IncludeSubDomains", "true"))
            .Preload.Should().BeFalse(
                "joining the browser preload list takes a form and leaving it takes months, so it is not a config flag");
    }

    [Fact]
    public void An_operator_who_has_checked_their_subdomains_can_turn_both_up()
    {
        var options = Configure(("Hsts:MaxAgeDays", "365"), ("Hsts:IncludeSubDomains", "true"));

        options.MaxAge.Should().Be(TimeSpan.FromDays(365));
        options.IncludeSubDomains.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_max_age_of_zero_or_less_is_refused(string days)
    {
        var act = () => Configure(("Hsts:MaxAgeDays", days));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxAgeDays*",
                "max-age=0 tells browsers to forget the policy, so it would switch HSTS off while looking like it switched it on");
    }
}
