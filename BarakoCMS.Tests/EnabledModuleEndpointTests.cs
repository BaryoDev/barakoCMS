using System.Net;
using System.Net.Http.Json;
using barakoCMS.Data;
using barakoCMS.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host that discovers every module this process references and enables one, booted the way
/// <c>BarakoCMS.Suite</c> boots, with FastEndpoints' own discovery left on.
/// </summary>
/// <remarks>
/// <c>BarakoTestHost</c> turns FastEndpoints' auto discovery off and names the endpoint assemblies
/// itself, so it cannot show what a real host maps. This host does not.
/// </remarks>
public sealed class EnabledModuleEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("barako_enabled_modules")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private WebApplication? _app;

    static EnabledModuleEndpointTests() =>
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
            ["InitialAdmin:Username"] = "admin",
            ["InitialAdmin:Password"] = $"Test-{Guid.NewGuid():N}!",
            ["Seed:DemoContent"] = "false",
            // DiscoveryDefault turns discovery off for this process through the environment.
            ["BarakoCMS:Modules:Discover"] = "true",
            ["BarakoCMS:Modules:Enabled"] = "Pwa",
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

    [Fact]
    public async Task The_one_enabled_module_answers()
    {
        var response = await _app!.GetTestClient().PostAsJsonAsync(
            "/api/pwa/report", new { deviceId = Guid.NewGuid().ToString("N"), displayMode = "standalone" },
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_module_left_off_the_enabled_list_has_no_endpoints()
    {
        var response = await _app!.GetTestClient().GetAsync("/api/accounting/accounts", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
