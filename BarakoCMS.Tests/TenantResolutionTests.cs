using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class TenantResolutionTests
{
    [Theory]
    [InlineData("rotary.example.com", "rotary")]
    [InlineData("a-club.example.com", "a-club")]
    [InlineData("ROTARY.example.com", "rotary")] // lower-cased
    [InlineData("example.com", null)]            // apex -> default
    [InlineData("localhost", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("www.example.com", null)]        // infra label -> default
    [InlineData("api.example.com", null)]
    [InlineData("admin.example.com", null)]
    [InlineData("", null)]
    public void ResolveSlug_maps_subdomain_to_tenant(string? host, string? expected)
    {
        TenantResolutionMiddleware.ResolveSlug(host).Should().Be(expected);
    }
}
