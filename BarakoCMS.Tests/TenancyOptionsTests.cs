using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The parts of the tenancy mode (#895) that need no host: how the setting is read, which routes
/// answer without a tenant, and what resolution leaves in <see cref="TenantContext"/>.
/// </summary>
public class TenancyOptionsTests
{
    private static IConfiguration Configuration(string? mode) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TenancyOptions.ModeKey] = mode })
            .Build();

    [Theory]
    [InlineData(null, TenancyMode.Single)]
    [InlineData("", TenancyMode.Single)]
    [InlineData("   ", TenancyMode.Single)]
    [InlineData("Single", TenancyMode.Single)]
    [InlineData("single", TenancyMode.Single)]
    [InlineData("Multi", TenancyMode.Multi)]
    [InlineData("MULTI", TenancyMode.Multi)]
    [InlineData(" multi ", TenancyMode.Multi)]
    public void The_mode_is_read_by_name_and_is_Single_when_nothing_is_set(string? configured, TenancyMode expected)
    {
        TenancyOptions.FromConfiguration(Configuration(configured)).Mode.Should().Be(expected);
    }

    /// <summary>
    /// A number is refused too. <c>Enum.TryParse</c> would read "1" as Multi and "7" as a mode
    /// that does not exist.
    /// </summary>
    [Theory]
    [InlineData("Mutli")]
    [InlineData("Both")]
    [InlineData("Single,Multi")]
    [InlineData("1")]
    [InlineData("7")]
    public void A_value_that_is_not_a_mode_is_refused_and_the_message_names_the_setting(string configured)
    {
        var act = () => TenancyOptions.FromConfiguration(Configuration(configured));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Tenancy:Mode is '{configured}'*Valid values: Single, Multi.");
    }

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/health/live")]
    [InlineData("HEAD", "/health/live")]
    [InlineData("GET", "/health/ready")]
    [InlineData("GET", "/health/build")]
    [InlineData("GET", "/metrics")]
    [InlineData("GET", "/api/meta")]
    [InlineData("GET", "/API/Meta")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/refresh")]
    [InlineData("GET", "/api/auth/google/callback")]
    [InlineData("GET", "/api/tenants/by-host/acme.example.com")]
    [InlineData("GET", "/api/tenants/acme/public")]
    [InlineData("HEAD", "/api/tenants/acme/public")]
    [InlineData("GET", "/api/tenants/tls-ask")]
    public void A_listed_route_answers_without_a_tenant(string method, string path)
    {
        TenantlessRoutes.Allows(method, new PathString(path)).Should().BeTrue();
    }

    /// <summary>
    /// A path is allowed for the methods its route serves. <c>/api/tenants/members/public</c> has
    /// the shape of the public profile route, and routing gives it to the member routes under
    /// <c>PUT</c> and <c>DELETE</c>, so <c>members</c> is not a handle under any method.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/api/contents")]
    [InlineData("GET", "/api/public/posts")]
    [InlineData("GET", "/api/me/tenants")]
    [InlineData("POST", "/api/me/switch")]
    [InlineData("GET", "/api/tenants")]
    [InlineData("POST", "/api/tenants")]
    [InlineData("GET", "/api/tenants/acme")]
    [InlineData("PUT", "/api/tenants/acme")]
    [InlineData("GET", "/api/tenants/members")]
    [InlineData("GET", "/api/tenants/members/roles")]
    [InlineData("GET", "/api/tenants/members/public")]
    [InlineData("PUT", "/api/tenants/members/public")]
    [InlineData("DELETE", "/api/tenants/members/public")]
    [InlineData("PUT", "/api/tenants/acme/public")]
    [InlineData("DELETE", "/api/tenants/acme/public")]
    [InlineData("POST", "/api/tenants/by-host/acme.example.com")]
    [InlineData("GET", "/api/tenants/by-host")]
    [InlineData("POST", "/api/tenants/tls-ask")]
    [InlineData("GET", "/api/tenants/tls-ask/more")]
    [InlineData("GET", "/api/tenants/acme/public/more")]
    [InlineData("POST", "/api/meta")]
    [InlineData("POST", "/health/live")]
    [InlineData("POST", "/metrics")]
    [InlineData("GET", "/api/authx")]
    [InlineData("GET", "/api/metadata")]
    [InlineData("GET", "/healthz")]
    [InlineData("GET", "/health-ui")]
    [InlineData("GET", "/metrics/more")]
    [InlineData("GET", "/swagger/index.html")]
    [InlineData("POST", "/api/webhooks/resend")]
    [InlineData("GET", "/api/a-route-added-tomorrow")]
    public void Any_other_route_needs_a_tenant(string method, string path)
    {
        TenantlessRoutes.Allows(method, new PathString(path)).Should().BeFalse();
    }

    /// <summary>
    /// Two tenants claiming one domain switch custom domains off until an operator fixes it. They
    /// must not switch the tenants off: in Multi a map with no active slugs answers 404 for
    /// every tenant, the one whose administrator has to fix the domain included.
    /// </summary>
    [Fact]
    public void A_domain_claimed_twice_drops_the_domains_and_keeps_the_active_tenants()
    {
        var first = new Tenant { Id = Guid.NewGuid(), Slug = "first", Name = "First", Domains = ["shared.example.com"] };
        var second = new Tenant { Id = Guid.NewGuid(), Slug = "second", Name = "Second", Domains = ["shared.example.com"] };

        var map = TenantDomainSource.Build([first, second], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        map.Count.Should().Be(0, "a domain two tenants claim routes to neither");
        map.Find("shared.example.com").Should().BeNull();
        map.IsActiveTenant("first").Should().BeTrue();
        map.IsActiveTenant("second").Should().BeTrue();
    }

    [Fact]
    public void Without_a_conflict_the_map_holds_the_domains_and_the_active_tenants()
    {
        var withDomain = new Tenant { Id = Guid.NewGuid(), Slug = "first", Name = "First", Domains = ["first.example.com"] };
        var without = new Tenant { Id = Guid.NewGuid(), Slug = "second", Name = "Second" };

        var map = TenantDomainSource.Build([withDomain, without], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        map.Find("first.example.com").Should().Be("first");
        map.IsActiveTenant("first").Should().BeTrue();
        map.IsActiveTenant("second").Should().BeTrue("a tenant with no domain is still a tenant");
    }

    [Fact]
    public void The_map_knows_an_active_slug_exactly_as_it_is_stored()
    {
        var map = new TenantDomainMap([], ["acme"]);

        map.IsActiveTenant("acme").Should().BeTrue();
        map.IsActiveTenant("Acme").Should().BeFalse("the caller lowercases, and the issuer matches the stored slug exactly");
        map.IsActiveTenant("other").Should().BeFalse();
        map.IsActiveTenant(null).Should().BeFalse();
        TenantDomainMap.Empty.IsActiveTenant("acme").Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Single", true)]
    [InlineData("Multi", false)]
    public void Background_work_visits_the_default_partition_except_in_Multi(string? mode, bool expected)
    {
        TenantPartitions.ServesDefaultPartition(Configuration(mode)).Should().Be(expected);
        TenantPartitions.ServesDefaultPartition(null).Should().BeTrue("a caller built without configuration is Single");
    }

    private sealed class FixedDomains(TenantDomainMap map) : ITenantDomainSource
    {
        public bool RefuseUnknownHosts => false;

        public Task<TenantDomainMap> GetAsync(CancellationToken ct = default) => Task.FromResult(map);

        public void Invalidate()
        {
        }
    }

    private sealed record Outcome(bool ReachedNext, string Slug, int StatusCode, string Body);

    private static Task<Outcome> ResolveAsync(string? mode, string path, string? header, params string[] activeSlugs) =>
        SendAsync(mode, HttpMethods.Get, path, header, [], activeSlugs);

    private static async Task<Outcome> SendAsync(
        string? mode, string method, string path, string? header, string[] alsoSent, string[] activeSlugs)
    {
        var services = new ServiceCollection()
            .AddSingleton(TenancyOptions.FromConfiguration(Configuration(mode)))
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        foreach (var name in alsoSent)
            context.Request.Headers[name] = "value";
        context.Request.Path = path;
        context.Request.Host = new HostString("localhost");
        context.Response.Body = new MemoryStream();
        if (header is not null)
            context.Request.Headers[TenantResolutionMiddleware.TenantHeader] = header;

        var tenant = new TenantContext();
        var reached = false;
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, tenant, new FixedDomains(new TenantDomainMap([], activeSlugs)));

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new Outcome(reached, tenant.Slug, context.Response.StatusCode, body);
    }

    [Fact]
    public async Task In_Multi_an_unlisted_route_under_an_unregistered_slug_stops_with_the_refusal()
    {
        var outcome = await ResolveAsync("Multi", "/api/contents", "ghost", "acme");

        outcome.ReachedNext.Should().BeFalse("nothing after resolution runs");
        outcome.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        outcome.Body.Should().Be(TenantResolutionMiddleware.NoTenantMessage);
    }

    /// <summary>
    /// A listed route is let through, and not on the slug the caller named: a slug nobody
    /// registered must not reach a session, an audit row or a cache key by way of sign-in.
    /// </summary>
    [Theory]
    [InlineData("ghost")]
    [InlineData(null)]
    public async Task In_Multi_a_listed_route_with_no_registered_tenant_runs_on_the_default_slug(string? header)
    {
        var outcome = await ResolveAsync("Multi", "/api/auth/login", header, "acme");

        outcome.ReachedNext.Should().BeTrue();
        outcome.Slug.Should().Be(Tenant.DefaultSlug);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/contents")]
    public async Task In_Multi_a_registered_tenant_named_in_any_case_is_resolved(string path)
    {
        var outcome = await ResolveAsync("Multi", path, " ACME ", "acme");

        outcome.ReachedNext.Should().BeTrue();
        outcome.Slug.Should().Be("acme");
    }

    /// <summary>
    /// Only a real preflight is let through: <c>OPTIONS</c> with <c>Origin</c> and
    /// <c>Access-Control-Request-Method</c>, which is what the CORS middleware answers itself.
    /// Any other <c>OPTIONS</c> would go on to routing on the default partition.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Origin")]
    [InlineData("Access-Control-Request-Method")]
    public async Task In_Multi_an_OPTIONS_request_that_is_not_a_preflight_is_refused(string? onlyHeader)
    {
        string[] alsoSent = onlyHeader is null ? [] : [onlyHeader];
        var outcome = await SendAsync("Multi", HttpMethods.Options, "/api/contents", null, alsoSent, ["acme"]);

        outcome.ReachedNext.Should().BeFalse();
        outcome.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        outcome.Body.Should().Be(TenantResolutionMiddleware.NoTenantMessage);
    }

    [Fact]
    public async Task In_Multi_a_preflight_is_let_through_on_the_default_slug()
    {
        var outcome = await SendAsync(
            "Multi", HttpMethods.Options, "/api/contents", "ghost", ["Origin", "Access-Control-Request-Method"], ["acme"]);

        outcome.ReachedNext.Should().BeTrue();
        outcome.Slug.Should().Be(Tenant.DefaultSlug);
    }

    /// <summary>The default: in Single an unregistered slug is resolved and served, as before.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Single")]
    public async Task In_Single_an_unregistered_slug_is_resolved_as_it_was(string? mode)
    {
        var outcome = await ResolveAsync(mode, "/api/contents", "ghost", "acme");

        outcome.ReachedNext.Should().BeTrue();
        outcome.Slug.Should().Be("ghost");
    }
}
