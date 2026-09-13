using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Data;
using barakoCMS.Extensions;
using BarakoCMS.DeviceTrust;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host that discovers every module this process references and enables two, booted the way
/// <c>BarakoCMS.Suite</c> boots, with FastEndpoints' own discovery left on.
/// </summary>
/// <remarks>
/// <c>BarakoTestHost</c> turns FastEndpoints' auto discovery off and names the endpoint assemblies
/// itself, so it cannot show what a real host maps. This host does not.
/// <para>
/// In the Sequential collection, and on the fixture's JWT key, for the reasons
/// <see cref="BarakoTestHostTests"/> gives: FastEndpoints keeps endpoint configuration and the
/// signing key in statics each host overwrites. Run in parallel, this host's startup left
/// DeviceEnforcementTests without its enforcement processor.
/// </para>
/// </remarks>
[Collection("Sequential")]
public sealed class EnabledModuleEndpointTests(EnabledModuleEndpointTests.Host host, IntegrationTestFixture fixture)
    : IClassFixture<EnabledModuleEndpointTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("barako_enabled_modules")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        private WebApplication? _app;

        static Host() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        public WebApplication App => _app ?? throw new InvalidOperationException("The host has not started.");

        public async ValueTask InitializeAsync()
        {
            await _postgres.StartAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["DATABASE_URL"] = string.Empty,
                ["JWT:Key"] = IntegrationTestFixture.JwtKey,
                ["JWT:Issuer"] = "BarakoTest",
                ["JWT:Audience"] = "BarakoClient",
                ["InitialAdmin:Username"] = "admin",
                ["InitialAdmin:Password"] = $"Test-{Guid.NewGuid():N}!",
                ["Seed:DemoContent"] = "false",
                // DiscoveryDefault turns discovery off for this process through the environment.
                ["BarakoCMS:Modules:Discover"] = "true",
                ["BarakoCMS:Modules:Enabled"] = "Pwa,DeviceTrust",
                ["DeviceTrust:Enforce"] = "true",
            });

            builder.Services.AddBarakoCMS(builder.Configuration);

            var app = builder.Build();
            app.UseBarakoCMS();
            await app.ApplyMartenSchemaAsync();
            await DataSeeder.SeedAsync(app);
            await app.RunBarakoModuleSeedersAsync();
            await app.StartAsync();
            _app = app;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
                await _app.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_enabled_module_answers()
    {
        var response = await host.App.GetTestClient().PostAsJsonAsync(
            "/api/pwa/report", new { deviceId = Guid.NewGuid().ToString("N"), displayMode = "standalone" },
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_module_left_off_the_enabled_list_has_no_endpoints()
    {
        var response = await host.App.GetTestClient().GetAsync("/api/accounting/accounts", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The positive control for the refusal below: the same user and endpoint, with a token that is
    /// not bound to a device, is served.
    /// </summary>
    [Fact]
    public async Task An_unbound_token_reaches_an_enabled_module_endpoint()
    {
        var userId = await SeedUserAsync();

        var response = await Client(fixture.CreateToken(["SuperAdmin"], userId.ToString()))
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A global processor from an enabled module still runs while another module is switched off.
    /// </summary>
    [Fact]
    public async Task An_enabled_module_global_processor_still_runs_beside_a_disabled_module()
    {
        var userId = await SeedUserAsync();
        var token = fixture.CreateToken(
            ["SuperAdmin"], userId.ToString(),
            new Dictionary<string, string> { [DeviceGate.DeviceClaim] = $"device-{Guid.NewGuid():n}" });

        var response = await Client(token).GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "DeviceTrust's enforcement processor refuses a bound token sent with no device header");
    }

    private HttpClient Client(string token)
    {
        var client = host.App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = host.App.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = id,
            Username = $"enabled-{id:n}",
            Email = $"enabled-{id:n}@example.com",
            PasswordHash = "not-used",
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
