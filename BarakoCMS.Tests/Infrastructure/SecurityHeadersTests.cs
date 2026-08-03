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
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData(null)]
    public void Style_src_keeps_unsafe_inline_everywhere_for_now(string? env)
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(env);

        csp.Should().Contain("style-src 'self' 'unsafe-inline'",
            "not yet verified whether HealthChecksUI's dashboard needs it — documented gap, not an oversight");
    }
}
