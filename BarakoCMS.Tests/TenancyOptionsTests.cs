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
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/build")]
    [InlineData("/metrics")]
    [InlineData("/api/meta")]
    [InlineData("/API/Meta")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/google/callback")]
    [InlineData("/api/tenants/by-host/acme.example.com")]
    [InlineData("/api/tenants/acme/public")]
    public void A_listed_route_answers_without_a_tenant(string path)
    {
        TenantlessRoutes.Allows(new PathString(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/api/contents")]
    [InlineData("/api/public/posts")]
    [InlineData("/api/me/tenants")]
    [InlineData("/api/me/switch")]
    [InlineData("/api/tenants")]
    [InlineData("/api/tenants/acme")]
    [InlineData("/api/tenants/members")]
    [InlineData("/api/tenants/members/roles")]
    [InlineData("/api/tenants/by-host")]
    [InlineData("/api/tenants/acme/public/more")]
    [InlineData("/api/authx")]
    [InlineData("/api/metadata")]
    [InlineData("/healthz")]
    [InlineData("/health-ui")]
    [InlineData("/metrics/more")]
    [InlineData("/swagger/index.html")]
    [InlineData("/api/webhooks/resend")]
    [InlineData("/api/a-route-added-tomorrow")]
    public void Any_other_route_needs_a_tenant(string path)
    {
        TenantlessRoutes.Allows(new PathString(path)).Should().BeFalse();
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

    private static async Task<Outcome> ResolveAsync(string? mode, string path, string? header, params string[] activeSlugs)
    {
        var services = new ServiceCollection()
            .AddSingleton(TenancyOptions.FromConfiguration(Configuration(mode)))
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
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
