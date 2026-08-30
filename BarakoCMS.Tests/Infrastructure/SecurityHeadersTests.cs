using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests.Infrastructure;

public class SecurityHeadersTests
{
    [Fact]
    public void Development_keeps_unsafe_inline_on_script_src_for_swagger_ui()
    {
        var csp = SecurityHeaders.ContentSecurityPolicy("Development");

        csp.Should().Contain("script-src 'self' 'unsafe-inline'",
            "Swagger UI only ever mounts in Development and needs it there");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Non_development_environments_drop_unsafe_inline_from_script_src(string? env)
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(env);

        csp.Should().Contain("script-src 'self';",
            "unsafe-inline in script-src is what defeats XSS mitigation — it must not ship outside Development");
        csp.Should().NotContain("script-src 'self' 'unsafe-inline'");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Non_development_environments_drop_unsafe_inline_from_style_src(string? env)
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(env);

        csp.Should().Contain("style-src 'self';",
            "attacker-controlled inline styles exfiltrate through selectors and background-image requests");
        csp.Should().NotContain("style-src 'self' 'unsafe-inline'");
    }

    [Fact]
    public void Development_keeps_unsafe_inline_on_style_src_for_swagger_ui()
    {
        SecurityHeaders.ContentSecurityPolicy("Development")
            .Should().Contain("style-src 'self' 'unsafe-inline'");
    }

    // The health dashboard's shipped bundle renders three dozen React style props, so its elements
    // carry inline style attributes and the page renders wrong under style-src 'self'. The allowance
    // is scoped to that one path instead of being the app-wide policy it used to be.
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void The_health_dashboard_keeps_unsafe_inline_on_style_src(string? env)
    {
        var csp = SecurityHeaders.HealthDashboardContentSecurityPolicy(env);

        csp.Should().Contain("style-src 'self' 'unsafe-inline'");
        csp.Should().Contain("script-src 'self';",
            "scoping the style allowance is not a licence to loosen scripts too");
    }

    [Theory]
    [InlineData("/health-ui", true)]
    [InlineData("/health-ui-api", true)]
    [InlineData("/health-ui/resources/healthchecksui-min.css", true)]
    [InlineData("/health", false)]
    [InlineData("/api/contents", false)]
    [InlineData("/", false)]
    [InlineData(null, false)]
    public void Only_the_dashboard_paths_get_the_looser_policy(string? path, bool expected)
    {
        SecurityHeaders.IsHealthDashboardPath(path).Should().Be(expected);
    }
}
