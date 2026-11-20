using System.Net;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class TestClientIpTests
{
    [Fact]
    public void Addresses_are_unique_and_inside_the_reserved_range()
    {
        var seen = Enumerable.Range(0, 1000).Select(_ => TestClientIp.Next()).ToList();

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().OnlyContain(a => IsReserved(a));
    }

    [Fact]
    public void A_client_gets_one_default_address_and_another_client_gets_another()
    {
        using var one = new HttpClient();
        using var two = new HttpClient();

        TestClientIp.Give(one);
        TestClientIp.Give(two);

        var first = one.DefaultRequestHeaders.GetValues(TestRemoteIpFilter.DefaultHeader).Single();
        var second = two.DefaultRequestHeaders.GetValues(TestRemoteIpFilter.DefaultHeader).Single();
        IsReserved(first).Should().BeTrue();
        first.Should().NotBe(second);
    }

    [Fact]
    public void The_default_does_not_touch_the_header_a_test_sets()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "192.0.2.5");

        TestClientIp.Give(client);

        client.DefaultRequestHeaders.GetValues(TestRemoteIpFilter.Header).Should().Equal("192.0.2.5");
    }

    [Fact]
    public void The_opt_out_is_not_an_address()
    {
        IPAddress.TryParse(TestClientIp.OptOut, out _).Should().BeFalse();
    }

    private static bool IsReserved(string address)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }
}
