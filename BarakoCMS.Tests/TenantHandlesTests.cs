using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class TenantHandlesTests
{
    [Theory]
    [InlineData("rckoronadal", true)]
    [InlineData("a-club", true)]
    [InlineData("club123", true)]
    [InlineData("ab", false)]        // too short
    [InlineData("-club", false)]     // leading hyphen
    [InlineData("club-", false)]     // trailing hyphen
    [InlineData("Club", false)]      // uppercase
    [InlineData("login", false)]     // reserved
    [InlineData("api", false)]       // reserved
    [InlineData("admin", false)]     // reserved
    [InlineData("", false)]
    public void IsValidHandle(string handle, bool expected) =>
        TenantHandles.IsValidHandle(handle).Should().Be(expected);

    [Theory]
    [InlineData("https://facebook.com/rckoronadal", true)]
    [InlineData("http://example.com", true)]
    [InlineData("facebook.com/x", false)]   // not absolute
    [InlineData("ftp://x.com", false)]      // wrong scheme
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void IsValidAbsoluteUrl(string url, bool expected) =>
        TenantHandles.IsValidAbsoluteUrl(url).Should().Be(expected);
}
