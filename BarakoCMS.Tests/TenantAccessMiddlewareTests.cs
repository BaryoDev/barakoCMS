using System.Security.Claims;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Which routes the tenant claim comparison skips. Only the public profile route is exempt, so a
/// token for one tenant is refused on any other route under another tenant's host.
/// </summary>
public class TenantAccessMiddlewareTests
{
    private sealed class FixedDomains(params string[] active) : ITenantDomainSource
    {
        public bool RefuseUnknownHosts => false;
        public void Invalidate() { }

        public Task<TenantDomainMap> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new TenantDomainMap([], active));
    }

    private static async Task<(bool Reached, int Status)> SendAsync(string path, bool multi = false)
    {
        var reached = false;
        var middleware = new TenantAccessMiddleware(_ => { reached = true; return Task.CompletedTask; },
            NullLogger<TenantAccessMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(new TenancyOptions { Mode = multi ? TenancyMode.Multi : TenancyMode.Single })
                .AddSingleton<ITenantDomainSource>(new FixedDomains("tenant-a", "tenant-b"))
                .BuildServiceProvider(),
        };
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant", "tenant-b")], authenticationType: "test"));

        await middleware.InvokeAsync(context, new TenantContext { Slug = "tenant-a" });
        return (reached, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/queries/public")]
    [InlineData("/api/connectors/public")]
    [InlineData("/api/requests/public")]
    [InlineData("/api/contents/by-slug/public")]
    [InlineData("/api/tenants/members/public")]
    public async Task A_token_for_another_tenant_is_refused_on_a_route_that_only_ends_in_public(string path)
    {
        var (reached, status) = await SendAsync(path);

        reached.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("/api/tenants/tenant-a/public")]
    [InlineData("/API/Tenants/tenant-a/Public")]
    public async Task A_token_for_another_tenant_can_read_the_public_profile(string path)
    {
        var (reached, _) = await SendAsync(path);

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task A_longer_path_under_the_public_profile_route_is_not_exempt()
    {
        var (reached, status) = await SendAsync("/api/tenants/tenant-a/public/more");

        reached.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task In_multi_mode_a_registered_tenants_token_can_read_the_public_profile_only()
    {
        var (profileReached, _) = await SendAsync("/api/tenants/tenant-a/public", multi: true);
        var (otherReached, otherStatus) = await SendAsync("/api/queries/public", multi: true);

        profileReached.Should().BeTrue();
        otherReached.Should().BeFalse();
        otherStatus.Should().Be(StatusCodes.Status403Forbidden);
    }
}
