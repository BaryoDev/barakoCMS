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
    private static async Task<(bool Reached, int Status)> SendAsync(string path)
    {
        var reached = false;
        var middleware = new TenantAccessMiddleware(_ => { reached = true; return Task.CompletedTask; },
            NullLogger<TenantAccessMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(new TenancyOptions()).BuildServiceProvider(),
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
    [InlineData("/api/tenants/tenant-a/public/more")]
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
}
