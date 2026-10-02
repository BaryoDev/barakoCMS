using System.Net;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Testing;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Where <c>IBarakoModule.ConfigureApp</c> puts a module's middleware in the real pipeline, shown
/// over HTTP: the probe writes what it could see into response headers.
/// </summary>
/// <remarks>
/// A host of its own, with three probe modules and the third left off the enabled list. In the
/// Sequential collection and on the fixture's JWT key for the reasons
/// <see cref="BarakoTestHostTests"/> gives.
/// </remarks>
[Collection("Sequential")]
public class ModuleConfigureAppTests : IClassFixture<ModuleConfigureAppTests.Host>
{
    private const string OrderHeader = "X-Probe-Order";
    private const string TenantHeader = "X-Probe-Tenant";
    private const string AuthenticatedHeader = "X-Probe-Authenticated";
    private const string EndpointHeader = "X-Probe-Endpoint";

    public sealed class Host : BarakoTestHost
    {
        public Host() : base(o =>
        {
            o.Modules.Add(new PipelineProbe("ProbeFirst"));
            o.Modules.Add(new PipelineProbe("ProbeSecond"));
            o.Modules.Add(new PipelineProbe("ProbeOff"));
            o.Settings["BarakoCMS:Modules:Enabled"] = "ProbeFirst,ProbeSecond";
            o.Settings["JWT:Key"] = IntegrationTestFixture.JwtKey;
        })
        {
        }
    }

    private sealed class PipelineProbe(string name) : IBarakoModule
    {
        public string Name { get; } = name;
        public bool HookRan { get; private set; }
        public bool HandedTheHostApplication { get; private set; }

        public void ConfigureApp(IApplicationBuilder app)
        {
            HookRan = true;
            HandedTheHostApplication = app is WebApplication or IEndpointRouteBuilder;

            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.Append(OrderHeader, Name);
                headers[TenantHeader] = context.RequestServices.GetRequiredService<TenantContext>().Slug;
                headers[AuthenticatedHeader] = (context.User.Identity?.IsAuthenticated == true).ToString();
                headers[EndpointHeader] = (context.GetEndpoint() is not null).ToString();
                await next(context);
            });
        }
    }

    private sealed class ThrowingProbe : IBarakoModule
    {
        public string Name => "ThrowingProbe";

        public void ConfigureApp(IApplicationBuilder app) =>
            throw new NotSupportedException("the hook failed");
    }

    private readonly Host _host;

    public ModuleConfigureAppTests(Host host) => _host = host;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private PipelineProbe Probe(string name) =>
        _host.Modules.OfType<PipelineProbe>().Single(p => p.Name == name);

    [Fact]
    public async Task Module_middleware_sees_the_resolved_tenant_and_the_authenticated_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = await _host.CreateTenantAsync(ct: ct);
        var client = await _host.CreateAdminClientAsync(slug, ct);

        var response = await client.GetAsync("/api/meta", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, TenantHeader).Should().Be(slug, "tenant resolution runs before module middleware");
        Header(response, AuthenticatedHeader).Should().Be(bool.TrueString, "authentication runs before module middleware");
        Header(response, EndpointHeader).Should().Be(bool.TrueString, "the endpoint is matched and has not run yet");
    }

    [Fact]
    public async Task An_anonymous_caller_on_an_endpoint_that_allows_it_reaches_module_middleware_unauthenticated()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _host.CreateClient().GetAsync("/api/tenants/by-host/localhost", ct);

        Header(response, EndpointHeader).Should().Be(bool.TrueString, "the route is a real anonymous endpoint");
        Header(response, AuthenticatedHeader).Should().Be(bool.FalseString);
        Header(response, TenantHeader).Should().Be(Tenant.DefaultSlug);
    }

    [Fact]
    public async Task A_request_refused_for_want_of_a_token_never_reaches_module_middleware()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _host.CreateClient().GetAsync("/api/meta", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Header(response, OrderHeader).Should().BeNull("UseAuthorization answers before module middleware runs");
    }

    [Fact]
    public async Task A_capability_refusal_happens_after_module_middleware()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await _host.CreateClientAsync("User", ct);

        var response = await user.GetAsync("/api/modules", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Header(response, AuthenticatedHeader).Should().Be(bool.TrueString,
            "the capability gate runs inside the endpoint, so module middleware sees a request the endpoint then refuses");
    }

    [Fact]
    public async Task Modules_run_in_registration_order_and_a_module_left_off_the_enabled_list_does_not_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await _host.CreateAdminClientAsync(ct);

        var response = await client.GetAsync("/api/meta", ct);

        var order = (Header(response, OrderHeader) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        order.Should().HaveCount(2);
        order.Should().Equal("ProbeFirst", "ProbeSecond");
    }

    [Fact]
    public void The_hook_is_called_for_enabled_modules_only()
    {
        Probe("ProbeFirst").HookRan.Should().BeTrue();
        Probe("ProbeSecond").HookRan.Should().BeTrue();
        Probe("ProbeOff").HookRan.Should().BeFalse("a module left off BarakoCMS:Modules:Enabled configures nothing");
    }

    [Fact]
    public void A_module_is_handed_a_branch_and_not_the_host_application()
    {
        Probe("ProbeFirst").HookRan.Should().BeTrue("the control: the flag below was set by a hook that ran");
        Probe("ProbeFirst").HandedTheHostApplication.Should().BeFalse(
            "the host's WebApplication would let a module map endpoints and reach the host itself");
    }

    [Fact]
    public async Task The_health_probes_skip_module_middleware()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _host.CreateClient().GetAsync("/health/live", ct);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the probe path is real");
        Header(response, OrderHeader).Should().BeNull();
    }

    [Fact]
    public async Task A_hook_that_throws_stops_startup_naming_the_module()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _host.ConnectionString,
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
            ["Seed:DemoContent"] = "false",
            ["BarakoCMS:Modules:Enabled"] = "ThrowingProbe",
        });
        builder.Services.AddBarakoCMS(builder.Configuration, modules =>
        {
            modules.Discover = false;
            modules.Add(new ThrowingProbe());
        });
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = [typeof(barakoCMS.Data.DataSeeder).Assembly];
        });
        await using var app = builder.Build();

        var use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*'ThrowingProbe'*ConfigureApp*")
            .WithInnerException<NotSupportedException>();
    }
}
