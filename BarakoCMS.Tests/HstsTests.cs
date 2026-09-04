using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Strict-Transport-Security as the running application actually sends it (#130). The nginx side of
/// the same check moved with the barakocms.com site to its own repository.
/// </summary>
/// <remarks>
/// Two defects sat behind the missing header. The application configured HSTS twice, once through
/// UseHsts and once by appending the header by hand, and a browser keeps the first value it is given,
/// so the effective policy was the framework's 30 day default rather than the one written in the
/// pipeline. The hand-written copy also ignored the environment, which pinned a developer's browser
/// against https://localhost. The deployments in front of the app sent nothing at all; the proxy
/// config and its checks live with the site now.
/// </remarks>
[Collection("Sequential")]
public class HstsTests
{
    private readonly IntegrationTestFixture _factory;

    public HstsTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// The policy the pipeline will apply, read from the host that AddBarakoCMS built. Asserting the
    /// registered options rather than a header is deliberate: UseHsts only runs outside Development
    /// and the test host is Development, so the header itself is not observable here, while a wrong
    /// or missing registration is.
    /// </summary>
    [Fact]
    public void The_host_registers_the_ninety_day_policy_rather_than_the_framework_default()
    {
        var options = _factory.Services.GetRequiredService<IOptions<HstsOptions>>().Value;

        options.MaxAge.Should().Be(TimeSpan.FromDays(HstsPolicy.DefaultMaxAgeDays),
            "the framework default is 30 days, so an unregistered policy looks like a working one");
        options.IncludeSubDomains.Should().BeFalse();
        options.Preload.Should().BeFalse();
    }

    /// <summary>
    /// Development has no TLS worth pinning, and a browser that accepts HSTS for localhost applies it
    /// to every other project the developer runs there.
    /// </summary>
    [Fact]
    public async Task Development_sends_no_hsts_header_even_over_https()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync("/health");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "UseHsts is only in the pipeline outside Development, and nothing else may write this header");
    }
}
