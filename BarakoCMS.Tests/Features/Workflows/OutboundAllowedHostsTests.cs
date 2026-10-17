using System.Net;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class OutboundAllowedHostsTests
{
    private static OutboundAddressGuard Guard(string resolvesTo, params string[] allowed) =>
        new(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolvesTo) }), allowedHosts: allowed);

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.18.0.5")]
    [InlineData("192.168.1.9")]
    [InlineData("127.0.0.1")]
    public async Task A_listed_host_may_resolve_to_an_internal_address(string address)
    {
        var chosen = await Guard(address, "press.internal").SelectAddressAsync("press.internal", CancellationToken.None);
        chosen.Should().Be(IPAddress.Parse(address));
    }

    [Fact]
    public async Task The_list_ignores_case()
    {
        var chosen = await Guard("10.1.2.3", "Press.Internal").SelectAddressAsync("press.INTERNAL", CancellationToken.None);
        chosen.Should().Be(IPAddress.Parse("10.1.2.3"));
    }

    [Fact]
    public async Task An_unlisted_host_on_an_internal_address_is_still_refused()
    {
        var act = () => Guard("10.1.2.3", "press.internal").SelectAddressAsync("other.internal", CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    [Fact]
    public async Task With_no_list_nothing_changes()
    {
        var act = () => Guard("10.1.2.3").SelectAddressAsync("press.internal", CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("fe80::1")]
    public async Task A_listed_host_still_cannot_reach_link_local_unspecified_or_multicast(string address)
    {
        var act = () => Guard(address, "press.internal").SelectAddressAsync("press.internal", CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>("listing a name must not open the metadata address");
    }

    [Fact]
    public async Task One_answer_in_a_blocked_class_refuses_the_host_even_when_another_answer_is_internal()
    {
        var guard = new OutboundAddressGuard(
            resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("169.254.169.254") }),
            allowedHosts: ["press.internal"]);
        var act = () => guard.SelectAddressAsync("press.internal", CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>();
    }
}
