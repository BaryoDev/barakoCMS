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

    // The next six tests share one setup: the host runs Production, and every other place that could
    // say Development does. A guard that took its answer from any of them would let the start
    // through. Each test names the guard it holds.

    private const string ValidHosts = "api.example.com";

    private static IServiceCollection ProductionHost() => new ServiceCollection().AsHost(Environments.Production);

    private static IConfiguration ConfigSaysDevelopment(params (string Key, string? Value)[] values) =>
        Config(
            [
                ("environment", "Development"),
                ("ENVIRONMENT", "Development"),
                ("ASPNETCORE_ENVIRONMENT", "Development"),
                ("DOTNET_ENVIRONMENT", "Development"),
                .. values,
            ]);

    /// <summary>Sets the process variable to Development for the test and puts back what was there.</summary>
    private sealed class ProcessSaysDevelopment : IDisposable
    {
        private const string Name = "ASPNETCORE_ENVIRONMENT";
        private readonly string? _before = Environment.GetEnvironmentVariable(Name);

        public ProcessSaysDevelopment() => Environment.SetEnvironmentVariable(Name, "Development");

        public void Dispose() => Environment.SetEnvironmentVariable(Name, _before);
    }

    [Fact]
    public void HostFilteringGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(), ConfigSaysDevelopment(("AllowedHosts", "*")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedHosts*");
    }

    [Fact]
    public void InitialAdminGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(),
            ConfigSaysDevelopment(("AllowedHosts", ValidHosts), ("InitialAdmin:Username", "admin"), ("InitialAdmin:Password", "short1!")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*InitialAdmin:Password*");
    }

    [Fact]
    public void DatabasePasswordGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(),
            ConfigSaysDevelopment(
                ("AllowedHosts", ValidHosts),
                ("ConnectionStrings:DefaultConnection", "Host=db;Database=d;Username=u;Password=postgres")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*database password*refused*");
    }

    [Fact]
    public void JwtKeyGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.AddJwtAndApiKeyAuth(
            ProductionHost(), ConfigSaysDevelopment(("JWT:Key", JwtKeyGuard.ComposeDevKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*outside Development*");
    }

    [Fact]
    public async Task DatabaseRoleGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();
        var reached = new InvalidOperationException("the guard asked the database");

        var act = async () => await DatabaseRoleGuard.AssertAsync(
            ConfigSaysDevelopment(),
            DevelopmentHost.Of(ProductionHost()),
            _ => throw reached,
            NullLogger.Instance,
            new object(),
            DatabaseRoleGuard.RoleQuery,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(reached);
    }

    [Fact]
    public void DemoAccounts_ReadTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();
        var environment = new HostingEnvironment { EnvironmentName = Environments.Production };

        DataSeeder.SeedsDemoAccounts(environment).Should().BeFalse();
        DataSeeder.SeedsDemoAccounts(new HostingEnvironment { EnvironmentName = Environments.Development }).Should().BeTrue();
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
