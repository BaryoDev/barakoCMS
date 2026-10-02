using System.Reflection;
using System.Threading.RateLimiting;
using barakoCMS.Extensions;
using barakoCMS.Modules;
using BarakoCMS.Forms;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>UseBarakoCMS</c> stops a host whose routes name a rate limit policy nobody registered, and a
/// host where two registrations add a policy of one name, and says what to change (#888).
/// </summary>
/// <remarks>
/// Each test builds its own <see cref="WebApplication"/> the way <c>JobWorkerSchemaOrderTests</c>
/// does, on a database of its own, and never starts it: the check runs inside
/// <c>UseBarakoCMS</c>. Endpoint discovery is off and the endpoint assemblies are named, so each
/// host maps exactly what its test says. A module's endpoints are only ever mapped here with the
/// module's services registered: FastEndpoints constructs every endpoint as it maps the routes, so
/// a host without those services stops there, before the check.
/// <para>
/// No app built here is disposed, and that is deliberate. The check runs last in
/// <c>UseBarakoCMS</c>, so every app here has mapped its FastEndpoints routes by then, the ones
/// expected to throw included. Mapping points FastEndpoints' process-wide service resolver at the
/// app that mapped last, and it stays there until another host maps. Code that resolves through
/// it outside a request, <c>JwtBearer.CreateToken</c> in the token issuer for one, then asks this
/// app's container, so a disposed one fails a later test in another class with
/// <c>ObjectDisposedException</c>. Left alive, the container answers. It is the rule
/// <c>IntegrationTestFixture.WithSetting</c> states for the hosts it builds.
/// </para>
/// </remarks>
[Collection("Sequential")]
public class RateLimitStartupCheckTests
{
    private const string FormsRoute = "api/public/forms/{slug}";

    private readonly IntegrationTestFixture _fixture;

    public RateLimitStartupCheckTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_host_route_naming_a_policy_nobody_defined_stops_the_host_naming_both()
    {
        var app = await BuildAsync();
        app.MapGet("/bookings/status", () => "ok").RequireRateLimiting("booking-status");

        Action use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*'/bookings/status' names 'booking-status'*");
    }

    [Fact]
    public async Task The_same_host_passes_once_configuration_defines_the_policy()
    {
        var app = await BuildAsync(setting: ("RateLimiting:Policies:booking-status:PermitLimit", "5"));
        app.MapGet("/bookings/status", () => "ok").RequireRateLimiting("booking-status");

        Action use = () => app.UseBarakoCMS();

        use.Should().NotThrow();
    }

    [Fact]
    public async Task A_registered_module_whose_route_names_its_own_policy_passes()
    {
        var app = await BuildAsync(module: new FormsModule());

        Action use = () => app.UseBarakoCMS();

        use.Should().NotThrow("the Forms module registers the policy its submit route names");
    }

    [Fact]
    public async Task A_registered_module_whose_route_names_a_missing_policy_stops_the_host()
    {
        var app = await BuildAsync(module: new FormsWithoutItsPolicy());

        Action use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>("a module's routes are checked like core's")
            .WithMessage($"*{FormsRoute}' names '{FormsOptions.RateLimitPolicy}'*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_that_registers_its_own_delivery_policy_is_told_the_name_is_reserved(bool beforeCore)
    {
        var app = await BuildAsync(
            beforeCore: beforeCore,
            own: services => services.Configure<RateLimiterOptions>(o =>
                o.AddPolicy("delivery", _ => RateLimitPartition.GetNoLimiter("own"))));

        Action use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*'delivery'*reserved*Rename*");
    }

    [Fact]
    public async Task A_configured_policy_with_the_name_of_a_module_policy_stops_the_host_naming_the_setting()
    {
        var app = await BuildAsync(
            module: new FormsModule(),
            setting: ("RateLimiting:Policies:forms:PermitLimit", "5"));

        Action use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*RateLimiting:Policies:forms*");
    }

    /// <summary>
    /// The Forms module with everything it registers except its rate limit policy, so its submit
    /// route names a policy that does not exist.
    /// </summary>
    private sealed class FormsWithoutItsPolicy : IBarakoModule
    {
        private readonly FormsModule _forms = new();

        public string Name => _forms.Name;

        public IEnumerable<Assembly> EndpointAssemblies => [typeof(FormsModule).Assembly];

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            var before = services.Count;
            _forms.ConfigureServices(services, configuration);

            for (var i = services.Count - 1; i >= before; i--)
            {
                if (services[i].ServiceType == typeof(IConfigureOptions<RateLimiterOptions>))
                    services.RemoveAt(i);
            }
        }
    }

    private async Task<WebApplication> BuildAsync(
        IBarakoModule? module = null,
        Action<IServiceCollection>? own = null,
        bool beforeCore = false,
        (string Key, string Value)? setting = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = await FreshDatabaseAsync(),
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
            ["Seed:DemoContent"] = "false",
        };
        if (setting is { } extra)
            values[extra.Key] = extra.Value;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(values);

        if (beforeCore)
            own?.Invoke(builder.Services);

        builder.Services.AddBarakoCMS(builder.Configuration, modules =>
        {
            modules.Discover = false;
            if (module is not null)
                modules.Add(module);
        });

        if (!beforeCore)
            own?.Invoke(builder.Services);

        var endpoints = new List<Assembly> { typeof(barakoCMS.Data.DataSeeder).Assembly };
        if (module is not null)
            endpoints.Add(typeof(FormsModule).Assembly);
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = endpoints.ToArray();
        });

        return builder.Build();
    }

    private async Task<string> FreshDatabaseAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "rate_limit_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }.ConnectionString;
    }
}
