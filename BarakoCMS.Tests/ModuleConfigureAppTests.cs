using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Testing;
using FastEndpoints;
using FluentAssertions;
using Marten;
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

    // A request that carries EchoRequestHeader has its value written back twice: before next, and
    // as the response starts. CookieRequestHeader also has the probe set a cookie before next.
    private const string EchoRequestHeader = "X-Probe-Echo";
    private const string CookieRequestHeader = "X-Probe-Set-Cookie";
    private const string BeforeNextHeader = "X-Probe-Before-Next";
    private const string OnStartingHeader = "X-Probe-On-Starting";

    public sealed class Host : BarakoTestHost
    {
        public Host() : base(o =>
        {
            o.Modules.Add(new ProbeFirst());
            o.Modules.Add(new ProbeSecond());
            o.Modules.Add(new ProbeOff());
            o.Settings["BarakoCMS:Modules:Enabled"] = "ProbeFirst,ProbeSecond";
            o.Settings["JWT:Key"] = IntegrationTestFixture.JwtKey;
            // Counts only requests an API key authenticated, and every other test here signs in
            // with a token or not at all.
            o.Settings["RateLimiting:ApiKey:PermitLimit"] = "2";
        })
        {
        }
    }

    // One class per probe: the module builder refuses two instances of one module class.
    private sealed class ProbeFirst() : PipelineProbe("ProbeFirst");

    private sealed class ProbeSecond() : PipelineProbe("ProbeSecond");

    private sealed class ProbeOff() : PipelineProbe("ProbeOff");

    private abstract class PipelineProbe(string name) : IBarakoModule
    {
        public string Name { get; } = name;
        public bool HookRan { get; private set; }
        public bool HandedTheHostApplication { get; private set; }

        /// <summary>Each hook core called, in the order it called them.</summary>
        public ConcurrentQueue<string> Calls { get; } = new();

        /// <summary>The echo value of every request that carried one and reached this middleware.</summary>
        public ConcurrentQueue<string> Echoes { get; } = new();

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
            Calls.Enqueue(nameof(ConfigureServices));

        public void ConfigureSchema(IModuleSchema schema) => Calls.Enqueue(nameof(ConfigureSchema));

        public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            Calls.Enqueue(nameof(SeedAsync));
            return Task.CompletedTask;
        }

        public void ConfigureApp(IApplicationBuilder app)
        {
            Calls.Enqueue(nameof(ConfigureApp));
            HookRan = true;
            HandedTheHostApplication = app is WebApplication or IEndpointRouteBuilder;

            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.Append(OrderHeader, Name);
                headers[TenantHeader] = context.RequestServices.GetRequiredService<TenantContext>().Slug;
                headers[AuthenticatedHeader] = (context.User.Identity?.IsAuthenticated == true).ToString();
                headers[EndpointHeader] = (context.GetEndpoint() is not null).ToString();

                var echo = context.Request.Headers[EchoRequestHeader].ToString();
                if (echo.Length > 0)
                {
                    Echoes.Enqueue(echo);
                    headers[BeforeNextHeader] = echo;
                    if (context.Request.Headers.ContainsKey(CookieRequestHeader))
                        headers.Append("Set-Cookie", "probe=1; Path=/");

                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers[OnStartingHeader] = echo;
                        return Task.CompletedTask;
                    });
                }

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

    /// <summary>Asks for the store from inside its pipeline hook.</summary>
    private sealed class StoreAsker : IBarakoModule
    {
        public string Name => "StoreAsker";

        public void ConfigureApp(IApplicationBuilder app) =>
            app.ApplicationServices.GetRequiredService<IDocumentStore>();
    }

    /// <summary>Configures a core document, which the schema surface refuses by module name.</summary>
    private sealed class SchemaBreaker : IBarakoModule
    {
        public string Name => "SchemaBreaker";

        public void ConfigureSchema(IModuleSchema schema) => schema.For<User>();
    }

    private readonly Host _host;

    public ModuleConfigureAppTests(Host host) => _host = host;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private PipelineProbe Probe(string name) =>
        _host.Modules.OfType<PipelineProbe>().Single(p => p.Name == name);

    /// <summary>
    /// A host built the way <see cref="Host"/> is, on its database, stopped short of
    /// <c>UseBarakoCMS</c> so a test can watch that call fail. Nothing before the hook connects.
    /// </summary>
    private WebApplication BuildUnstarted(params IBarakoModule[] modules)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _host.ConnectionString,
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
            ["Seed:DemoContent"] = "false",
            ["BarakoCMS:Modules:Enabled"] = string.Join(",", modules.Select(m => m.Name)),
        });
        builder.Services.AddBarakoCMS(builder.Configuration, registered =>
        {
            registered.Discover = false;
            foreach (var module in modules)
                registered.Add(module);
        });
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = [typeof(barakoCMS.Data.DataSeeder).Assembly];
        });
        return builder.Build();
    }

    /// <summary>Stores a redirect and returns the anonymous, output cached route that resolves it.</summary>
    private async Task<string> CachedRouteAsync(CancellationToken ct)
    {
        var from = "/old-" + Guid.NewGuid().ToString("N")[..10];
        var admin = await _host.CreateAdminClientAsync(ct);

        var save = await admin.PostAsJsonAsync("/api/redirects",
            new { fromPath = from, toPath = "/new-home", permanent = true }, ct);
        save.IsSuccessStatusCode.Should().BeTrue(await save.Content.ReadAsStringAsync(ct));

        return $"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}";
    }

    private async Task<HttpResponseMessage> GetWithEchoAsync(string url, string echo, bool cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(EchoRequestHeader, echo);
        if (cookie)
            request.Headers.Add(CookieRequestHeader, "1");

        var response = await _host.CreateClient().SendAsync(request, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return response;
    }

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

    /// <summary>
    /// Pins the side of module middleware the API key quota sits on. With the two swapped, the
    /// refused request reaches the probe and its 429 carries the probe's header.
    /// </summary>
    [Fact]
    public async Task A_request_over_the_api_key_quota_never_reaches_module_middleware()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await StoreApiKeyAsync(ct);
        var echoes = new[] { $"one-{Guid.NewGuid():N}", $"two-{Guid.NewGuid():N}", $"three-{Guid.NewGuid():N}" };

        var responses = new List<HttpResponseMessage>();
        foreach (var echo in echoes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add(EchoRequestHeader, echo);
            responses.Add(await _host.CreateClient().SendAsync(request, ct));
        }

        responses.Should().HaveCount(3);
        responses[0].StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        responses[1].StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        responses[2].StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the host allows this key two requests a minute");
        Header(responses[2], OrderHeader).Should().BeNull("the quota answers before module middleware runs");

        var seen = Probe("ProbeFirst").Echoes.Where(e => Array.IndexOf(echoes, e) >= 0).ToArray();
        seen.Should().HaveCount(2, "the control: the two requests within the quota reached the module");
        seen.Should().Equal(echoes[0], echoes[1]);
    }

    /// <summary>An API key owned by the seeded admin on the default tenant. Returns the secret.</summary>
    private async Task<string> StoreApiKeyAsync(CancellationToken ct)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await using var session = _host.OpenSession();
        var admin = await session.Query<User>().FirstAsync(u => u.Username == _host.AdminUsername, ct);
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "quota order",
            KeyHash = barakoCMS.Infrastructure.Auth.ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = admin.Id,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = ["*"],
        });
        await session.SaveChangesAsync(ct);
        return secret;
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
    public async Task A_request_that_matches_no_endpoint_reaches_module_middleware_with_no_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _host.CreateClient().GetAsync($"/api/no-such-route-{Guid.NewGuid():N}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Header(response, EndpointHeader).Should().Be(bool.FalseString);
        Header(response, AuthenticatedHeader).Should().Be(bool.FalseString);
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

    /// <summary>
    /// Pins the side of core's output cache the hook sits on. Below the cache, the second request
    /// is answered from the cache and never reaches module middleware.
    /// </summary>
    [Fact]
    public async Task Module_middleware_runs_on_a_request_core_answers_from_its_output_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await CachedRouteAsync(ct);
        var one = $"one-{Guid.NewGuid():N}";
        var two = $"two-{Guid.NewGuid():N}";

        var first = await GetWithEchoAsync(url, one, cookie: false, ct);
        var second = await GetWithEchoAsync(url, two, cookie: false, ct);

        Header(first, BeforeNextHeader).Should().Be(one);
        Header(second, BeforeNextHeader).Should().Be(one,
            "the control: the second response came from the output cache, which stored the header the "
          + "module wrote before next and replays it over what the module wrote for this request");

        var seen = Probe("ProbeFirst").Echoes.Where(e => e == one || e == two).ToArray();
        seen.Should().HaveCount(2, "module middleware sits outside the cache, so it runs on a hit too");
        seen.Should().Equal(one, two);
    }

    [Fact]
    public async Task A_header_a_module_writes_as_the_response_starts_is_its_own_on_a_cached_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await CachedRouteAsync(ct);
        var one = $"one-{Guid.NewGuid():N}";
        var two = $"two-{Guid.NewGuid():N}";

        var first = await GetWithEchoAsync(url, one, cookie: false, ct);
        var second = await GetWithEchoAsync(url, two, cookie: false, ct);

        Header(second, BeforeNextHeader).Should().Be(one, "the control: the second response came from the output cache");
        Header(first, OnStartingHeader).Should().Be(one);
        Header(second, OnStartingHeader).Should().Be(two);
    }

    [Fact]
    public async Task A_cookie_a_module_sets_before_next_stops_core_caching_the_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await CachedRouteAsync(ct);
        var one = $"one-{Guid.NewGuid():N}";
        var two = $"two-{Guid.NewGuid():N}";

        await GetWithEchoAsync(url, one, cookie: true, ct);
        var second = await GetWithEchoAsync(url, two, cookie: true, ct);

        // The same route replays the first header when it is cached, which the two tests above
        // show. Here the second response carries its own, so the first was never stored.
        Header(second, BeforeNextHeader).Should().Be(two);
    }

    [Fact]
    public void Core_calls_the_hooks_in_the_order_the_contract_states()
    {
        var calls = Probe("ProbeFirst").Calls.Distinct().ToArray();

        calls.Should().HaveCount(4);
        calls.Should().Equal(
            nameof(IBarakoModule.ConfigureServices),
            nameof(IBarakoModule.ConfigureSchema),
            nameof(IBarakoModule.ConfigureApp),
            nameof(IBarakoModule.SeedAsync));
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
        await using var app = BuildUnstarted(new ThrowingProbe());

        var use = () => app.UseBarakoCMS();

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*'ThrowingProbe'*ConfigureApp*")
            .WithInnerException<NotSupportedException>();
    }

    /// <summary>
    /// The store is built before any pipeline hook. Built lazily, the first hook to ask for it runs
    /// every module's <c>ConfigureSchema</c>, and the refusal is reported as that module's failure.
    /// </summary>
    [Fact]
    public async Task A_refused_schema_is_reported_as_its_own_module_and_not_as_the_hook_that_asked_for_the_store()
    {
        await using var app = BuildUnstarted(new StoreAsker(), new SchemaBreaker());

        var use = () => app.UseBarakoCMS();

        // Every message in the chain, so the test does not depend on what wraps the refusal.
        var messages = new List<string>();
        for (Exception? ex = use.Should().Throw<Exception>().Which; ex is not null; ex = ex.InnerException)
            messages.Add(ex.Message);

        messages.Should().NotBeEmpty();
        messages.Should().Contain(m => m.Contains("SchemaBreaker"));
        messages.Should().NotContain(m => m.Contains("StoreAsker"));
        messages.Should().NotContain(m => m.Contains(nameof(IBarakoModule.ConfigureApp)));
    }
}
