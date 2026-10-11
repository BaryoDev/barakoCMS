using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

public class CallerCapabilitiesTests
{
    private readonly IPermissionResolver _resolver = Substitute.For<IPermissionResolver>();

    private static ClaimsPrincipal Signed(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public async Task HoldsAsync_UserIdHoldingCapability_True()
    {
        var id = Guid.NewGuid();
        _resolver.HasCapabilityAsync(id, "read_x", Arg.Any<CancellationToken>()).Returns(true);

        var holds = await new CallerCapabilities(_resolver).HoldsAsync(Signed(new Claim("UserId", id.ToString())), "read_x");

        holds.Should().BeTrue();
    }

    [Fact]
    public async Task HoldsAsync_NoUserId_FalseWithoutAskingResolver()
    {
        var holds = await new CallerCapabilities(_resolver).HoldsAsync(Signed(new Claim("Username", "x")), "read_x");

        holds.Should().BeFalse();
        await _resolver.DidNotReceiveWithAnyArgs().HasCapabilityAsync(default, default!, default);
    }

    [Fact]
    public async Task HoldsAsync_Anonymous_False()
    {
        var holds = await new CallerCapabilities(_resolver).HoldsAsync(new ClaimsPrincipal(new ClaimsIdentity()), "read_x");

        holds.Should().BeFalse();
    }

    [Fact]
    public async Task HoldsAsync_StationHoldingCapability_TrueWithoutAskingResolver()
    {
        var station = Signed(new Claim("auth_method", "station"), new Claim("station_cap", "read_x"));

        (await new CallerCapabilities(_resolver).HoldsAsync(station, "read_x")).Should().BeTrue();
        (await new CallerCapabilities(_resolver).HoldsAsync(station, "read_y")).Should().BeFalse();
        await _resolver.DidNotReceiveWithAnyArgs().HasCapabilityAsync(default, default!, default);
    }
}
