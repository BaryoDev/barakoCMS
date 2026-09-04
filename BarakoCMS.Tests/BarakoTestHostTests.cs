using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BarakoCMS.Portability;
using BarakoCMS.Testing;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>BarakoCMS.Testing</c>, proved on a real module. Portability is the smallest first-party one:
/// one capability-gated endpoint, a seeder that grants Admin its capabilities, and no settings.
/// </summary>
/// <remarks>
/// A fixture that stood in for the host it doubles is what the main suite got wrong for a long
/// time (roles with no capabilities, modules with no seeds), so every claim here is made over HTTP
/// against the pipeline a deployment runs, with the roles the deployment's seeder makes.
/// </remarks>
public class BarakoTestHostTests : IClassFixture<BarakoTestHostTests.Host>
{
    public sealed class Host : BarakoTestHost
    {
        public Host() : base(o =>
        {
            o.Modules.Add(new PortabilityModule());
            o.Modules.Add(new SettingsProbe());
            o.Settings["Modules:SettingsProbe:Greeting"] = "from the scoped section";
            o.Settings["Greeting"] = "from the root";
            o.Settings["JWT:Key"] = SharedJwtKey;
        })
        {
        }
    }

    /// <summary>
    /// FastEndpoints validates through one process-wide signing key, overwritten by whichever host
    /// materialises its bearer options last. This class runs beside IntegrationTestFixture, which
    /// validates through that key, so every host started here uses the fixture's key: a host with a
    /// random one would turn the fixture's tokens into 401s for the rest of the run.
    /// </summary>
    private const string SharedJwtKey = IntegrationTestFixture.JwtKey;

    /// <summary>Keeps the configuration it was handed, so the test can see which section that was.</summary>
    private sealed class SettingsProbe : IBarakoModule
    {
        public string Name => "SettingsProbe";
        public string? Greeting { get; private set; }

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
            Greeting = configuration["Greeting"];
    }

    private readonly Host _host;

    public BarakoTestHostTests(Host host) => _host = host;

    [Fact]
    public async Task The_seeded_admin_reaches_a_registered_module_endpoint()
    {
        var client = await _host.CreateAdminClientAsync(TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        bundle.TryGetProperty("contentTypes", out _).Should().BeTrue("the module answered with its own bundle shape");
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _host.CreateClient().GetAsync("/api/portability/export", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_Admin_reaches_the_module_through_the_capability_its_seeder_granted()
    {
        // Admin alone, not the seeded admin: that one is also SuperAdmin, whose wildcard satisfies
        // any capability and so proves nothing about the module's own seed.
        var admin = await _host.CreateClientAsync("Admin", TestContext.Current.CancellationToken);
        var user = await _host.CreateClientAsync("User", TestContext.Current.CancellationToken);

        var reached = await admin.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);
        var refused = await user.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);

        reached.StatusCode.Should().Be(HttpStatusCode.OK, "PortabilityModule.SeedAsync grants export_content to Admin");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "User was never granted anything by the module");
    }

    [Fact]
    public async Task The_host_lists_the_modules_it_runs()
    {
        var client = await _host.CreateAdminClientAsync(TestContext.Current.CancellationToken);

        var response = await client.GetFromJsonAsync<JsonElement>("/api/modules", TestContext.Current.CancellationToken);

        var names = response.GetProperty("items").EnumerateArray()
            .Where(m => m.GetProperty("enabled").GetBoolean())
            .Select(m => m.GetProperty("name").GetString())
            .ToList();
        names.Should().HaveCount(2);
        names.Should().BeEquivalentTo("Portability", "SettingsProbe");
    }

    [Fact]
    public void A_module_receives_its_own_configuration_section()
    {
        var probe = _host.Modules.OfType<SettingsProbe>().Single();

        probe.Greeting.Should().Be("from the scoped section",
            "a module is handed Modules:{Name}, never the application root");
    }

    [Fact]
    public async Task A_tenant_admin_client_works_in_its_tenant_and_a_default_tenant_token_does_not()
    {
        var slug = await _host.CreateTenantAsync(ct: TestContext.Current.CancellationToken);
        var tenantAdmin = await _host.CreateAdminClientAsync(slug, TestContext.Current.CancellationToken);
        var defaultAdmin = await _host.CreateAdminClientAsync(TestContext.Current.CancellationToken);
        defaultAdmin.DefaultRequestHeaders.Add("X-Tenant", slug);

        var own = await tenantAdmin.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);
        var replayed = await defaultAdmin.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);

        own.StatusCode.Should().Be(HttpStatusCode.OK, "the admin was given a membership and a token for that tenant");
        replayed.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a token minted for the default tenant is not valid on another");
    }

    [Fact]
    public async Task A_tenant_admin_client_leaves_the_admin_able_to_sign_in_on_that_tenant()
    {
        // The minted token skips the issuer, so the membership the helper creates is only visible
        // through the path a real client takes: the login endpoint refuses a registered tenant the
        // user holds no active membership in.
        var slug = await _host.CreateTenantAsync(ct: TestContext.Current.CancellationToken);
        await _host.CreateAdminClientAsync(slug, TestContext.Current.CancellationToken);

        var anonymous = _host.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", slug);
        var response = await anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = _host.AdminUsername, password = _host.AdminPassword },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "CreateAdminClientAsync(slug) gave the admin an active membership in the tenant");
    }

    [Fact]
    public void The_host_validates_tokens_with_its_own_key_rather_than_the_process_wide_one()
    {
        // A second host with a different key would prove this end to end, and would also overwrite
        // the process-wide key under IntegrationTestFixture and fail the rest of the suite, which
        // is what happened on the first CI run. So the validation parameters are read instead: no
        // resolver, which is what reads the static, and this host's own key in its place.
        var parameters = _host.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;

        parameters.IssuerSigningKeyResolver.Should().BeNull("the resolver reads FastEndpoints' static key");
        parameters.IssuerSigningKey.Should().BeOfType<SymmetricSecurityKey>()
            .Which.Key.Should().Equal(Encoding.ASCII.GetBytes(_host.JwtKey));
    }

    [Fact]
    public async Task A_configured_JWT_key_is_the_one_the_clients_sign_with()
    {
        _host.JwtKey.Should().Be(SharedJwtKey, "the options set it");

        var client = await _host.CreateAdminClientAsync(TestContext.Current.CancellationToken);
        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a token signed with a key the host does not validate with is a 401");
    }

    [Fact]
    public async Task A_host_running_no_modules_does_not_serve_the_module_endpoint()
    {
        // Registration is what puts a module's endpoints in the host, so the same request on a host
        // without the module has to miss. Its own container, so it costs a few seconds.
        await using var bare = new BarakoTestHost(o => o.Settings["JWT:Key"] = SharedJwtKey);
        await bare.InitializeAsync();

        var client = await bare.CreateAdminClientAsync(TestContext.Current.CancellationToken);
        var response = await client.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
