using barakoCMS.Data;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Security;

/// <summary>
/// The protections a fresh production start has with no configuration, one named test each.
/// </summary>
public class SecureDefaultsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Csp_HasFrameAncestorsNoneAndTheOtherLockedDownDirectives(string? env)
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(env);

        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("base-uri 'self'");
        csp.Should().Contain("form-action 'self'");
        csp.Should().Contain("object-src 'none'");
    }

    [Fact]
    public void HealthDashboardCsp_KeepsTheLockedDownDirectives()
    {
        var csp = SecurityHeaders.HealthDashboardContentSecurityPolicy("Production");

        csp.Should().Contain("style-src 'self' 'unsafe-inline'");
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("object-src 'none'");
    }

    [Fact]
    public void DevelopmentCsp_IsUnchangedForSwagger()
    {
        SecurityHeaders.ContentSecurityPolicy("Development").Should().NotContain("frame-ancestors");
    }

    [Fact]
    public void PermissionsPolicy_DeniesCameraMicrophoneAndGeolocation()
    {
        SecurityHeaders.PermissionsPolicy.Should().Contain("camera=()");
        SecurityHeaders.PermissionsPolicy.Should().Contain("microphone=()");
        SecurityHeaders.PermissionsPolicy.Should().Contain("geolocation=()");
    }

    [Fact]
    public void CrossOriginPolicies_AreSameOriginExceptForFiles()
    {
        SecurityHeaders.CrossOriginOpenerPolicy.Should().Be("same-origin");
        SecurityHeaders.CrossOriginResourcePolicy.Should().Be("same-origin");
        SecurityHeaders.CrossOriginResourcePolicyForFiles.Should().Be("cross-origin");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("api.example.com;*")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("api.example.com;0.0.0.0")]
    [InlineData("[::];api.example.com")]
    public void OpenAllowedHosts_StopsProductionStartup(string? allowedHosts)
    {
        var act = () => HostFilteringGuard.Validate(Config(("AllowedHosts", allowedHosts)), isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedHosts*Security:AllowAnyHost*");
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("api.example.com;cms.example.com")]
    [InlineData("*.example.com")]
    public void RealAllowedHosts_StartInProduction(string allowedHosts)
    {
        var warnings = new List<string>();

        HostFilteringGuard.Validate(Config(("AllowedHosts", allowedHosts)), isDevelopment: false, warnings.Add);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public void AllowAnyHost_StartsInProductionAndWarnsOnce()
    {
        var warnings = new List<string>();

        HostFilteringGuard.Validate(
            Config(("AllowedHosts", "*"), ("Security:AllowAnyHost", "true")), isDevelopment: false, warnings.Add);

        warnings.Should().ContainSingle().Which.Should().Contain("Security:AllowAnyHost");
    }

    [Fact]
    public void OpenAllowedHosts_StartsInDevelopment()
    {
        var act = () => HostFilteringGuard.Validate(Config(("AllowedHosts", "*")), isDevelopment: true);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("short1!", "admin")]
    [InlineData("elevenchars", "admin")]
    public void WeakInitialAdminPassword_StopsStartup_WhenTooShort(string password, string user)
    {
        var act = () => InitialAdminGuard.Validate(user, password, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*InitialAdmin:Password*12 characters*");
    }

    [Theory]
    [InlineData("site-administrator", "site-administrator")]
    [InlineData("Site-Administrator", "site-administrator")]
    [InlineData("site-administrator@example.com", "site-administrator")]
    public void WeakInitialAdminPassword_StopsStartup_WhenItIsTheUserNameOrEmail(string password, string user)
    {
        var act = () => InitialAdminGuard.Validate(user, password, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*user name or its email*");
    }

    [Theory]
    [InlineData("changeme-in-production")]
    [InlineData("change-me-to-a-strong-password")]
    [InlineData("local-only-password")]
    [InlineData("REPLACE_THIS_WITH_A_REAL_ADMIN_PASSWORD")]
    [InlineData("YOUR_ADMIN_PASSWORD")]
    public void WeakInitialAdminPassword_StopsStartup_WhenItIsAnExampleValue(string password)
    {
        var act = () => InitialAdminGuard.Validate("someone-else", password, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*example or default*");
    }

    [Fact]
    public void WeakInitialAdminPassword_StopsStartup_WhenItIsOnlyWhitespace()
    {
        var act = () => InitialAdminGuard.Validate("someone-else", new string(' ', 12), isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*InitialAdmin:Password*");
    }

    [Fact]
    public void WeakInitialAdminPassword_StopsStartup_WhenAnExampleIsPaddedWithSpaces()
    {
        var act = () => InitialAdminGuard.Validate("someone-else", " changeme-in-production ", isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*example or default*");
    }

    [Fact]
    public void WeakInitialAdminPassword_NeverRepeatsThePassword()
    {
        const string password = "changeme-in-production";

        var act = () => InitialAdminGuard.Validate("someone-else", password, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain(password);
    }

    [Theory]
    [InlineData("k3ep-this-one-out-7Q", "admin")]
    [InlineData("exactly12char", "admin")]
    public void GoodInitialAdminPassword_Starts(string password, string user)
    {
        var act = () => InitialAdminGuard.Validate(user, password, isDevelopment: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void UnsetInitialAdminPassword_Starts_BecauseTheSeederGeneratesOne()
    {
        var act = () => InitialAdminGuard.Validate("admin", null, isDevelopment: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void WeakInitialAdminPassword_StartsInDevelopment()
    {
        var act = () => InitialAdminGuard.Validate("admin", "admin", isDevelopment: true);

        act.Should().NotThrow();
    }
}
