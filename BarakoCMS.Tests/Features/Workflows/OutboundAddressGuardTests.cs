using System.Net;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The guard resolves a name once and dials an address that answer survived, so a name whose DNS
/// answer changes between the check and the connection cannot move the connection (#258).
/// </summary>
/// <remarks>
/// Driven with a scripted resolver and a recording connect, so the assertion is on which address was
/// dialled rather than on whether something far away happened to be reachable from the test host.
///
/// Every refusal here is paired with a case that connects. A guard that refuses everything satisfies
/// the refusal tests on its own and would be worse than the hole it is meant to close.
/// </remarks>
public class OutboundAddressGuardTests
{
    private const string PublicAddress = "203.0.113.10";
    private const string MetadataAddress = "169.254.169.254";

    [Fact]
    public async Task A_name_is_resolved_once_and_dialled_on_the_address_that_was_checked()
    {
        var answers = new Queue<string[]>([[PublicAddress], [MetadataAddress]]);
        var resolutions = 0;
        IPAddress? dialled = null;

        var guard = new OutboundAddressGuard(
            resolve: (_, _) =>
            {
                resolutions++;
                return Task.FromResult(Array.ConvertAll(answers.Dequeue(), IPAddress.Parse));
            },
            connect: (address, _, _) =>
            {
                dialled = address;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        await guard.ConnectAsync(new DnsEndPoint("rebind.example", 80), CancellationToken.None);

        resolutions.Should().Be(1, "a second lookup is the window a rebinding answer moves through");
        dialled.Should().Be(IPAddress.Parse(PublicAddress));
        dialled.Should().NotBe(IPAddress.Parse(MetadataAddress));
    }

    [Fact]
    public async Task A_name_answering_with_the_metadata_address_is_never_dialled()
    {
        var dialled = new List<IPAddress>();
        var guard = GuardAnswering([MetadataAddress], dialled);

        var connect = async () => await guard.ConnectAsync(new DnsEndPoint("evil.example", 80), CancellationToken.None);

        await connect.Should().ThrowAsync<HttpRequestException>();
        dialled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_answering_with_one_public_and_one_private_address_is_never_dialled()
    {
        var dialled = new List<IPAddress>();
        var guard = GuardAnswering([PublicAddress, "10.1.2.3"], dialled);

        var connect = async () => await guard.ConnectAsync(new DnsEndPoint("mixed.example", 80), CancellationToken.None);

        await connect.Should().ThrowAsync<HttpRequestException>(
            "a mixed answer is what a rebinding attempt looks like, so taking the public one would make it a retry away");
        dialled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_answering_with_a_public_address_is_dialled()
    {
        var dialled = new List<IPAddress>();
        var guard = GuardAnswering([PublicAddress], dialled);

        await guard.ConnectAsync(new DnsEndPoint("ok.example", 80), CancellationToken.None);

        dialled.Should().ContainSingle().Which.Should().Be(IPAddress.Parse(PublicAddress));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.7")]
    [InlineData("100.100.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void A_blocked_address_is_blocked_however_it_is_written(string address)
        => OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.10")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void A_public_address_is_allowed(string address)
        => OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(address)).Should().BeFalse();

    private static OutboundAddressGuard GuardAnswering(string[] answer, List<IPAddress> dialled) =>
        new(
            resolve: (_, _) => Task.FromResult(Array.ConvertAll(answer, IPAddress.Parse)),
            connect: (address, _, _) =>
            {
                dialled.Add(address);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
}
