using System.Net;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("240.0.0.1")]
    [InlineData("fec0::1")]
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

    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("::a9fe:a9fe")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("::7f00:1")]
    [InlineData("::a00:1")]
    [InlineData("64:ff9b::c0a8:101")]
    public void Wrapped_forms_of_blocked_addresses_are_blocked(string address)
    {
        OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("::a9fe:a9fe")]
    public async Task A_listed_host_cannot_reach_a_wrapped_link_local_address(string address)
    {
        var act = () => Guard(address, "press.internal").SelectAddressAsync("press.internal", CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    [Theory]
    [InlineData("64:ff9b::808:808")]
    [InlineData("::ffff:8.8.8.8")]
    public void Wrapped_public_addresses_stay_allowed(string address)
    {
        OutboundAddressGuard.IsBlockedAddress(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Theory]
    [InlineData("press.internal", 443, true)]
    [InlineData("press.internal", 80, true)]
    [InlineData("press.internal", 5432, false)]
    [InlineData("press.internal", 8080, false)]
    public async Task An_entry_without_a_port_allows_only_80_and_443(string host, int port, bool allowed)
    {
        (await Guard("10.1.2.3", "press.internal").IsHostAllowedAsync(host, port, CancellationToken.None)).Should().Be(allowed);
    }

    [Theory]
    [InlineData(3000, true)]
    [InlineData(443, false)]
    [InlineData(80, false)]
    [InlineData(3001, false)]
    public async Task An_entry_with_a_port_allows_only_that_port(int port, bool allowed)
    {
        (await Guard("10.1.2.3", "press.internal:3000").IsHostAllowedAsync("PRESS.internal", port, CancellationToken.None)).Should().Be(allowed);
    }

    [Fact]
    public async Task Entries_for_one_host_add_up()
    {
        var guard = Guard("10.1.2.3", "press.internal:3000", "press.internal:3001");
        (await guard.IsHostAllowedAsync("press.internal", 3001, CancellationToken.None)).Should().BeTrue();
        (await guard.IsHostAllowedAsync("press.internal", 3002, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_malformed_port_drops_the_entry()
    {
        var guard = Guard("10.1.2.3", "press.internal:abc");
        (await guard.IsHostAllowedAsync("press.internal", 443, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_dialled_port_is_checked()
    {
        var guard = new OutboundAddressGuard(
            resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }),
            connect: (_, _, _) => ValueTask.FromResult<Stream>(Stream.Null),
            allowedHosts: ["press.internal:3000"]);
        await guard.ConnectAsync(new DnsEndPoint("press.internal", 3000), CancellationToken.None);
        var act = async () => await guard.ConnectAsync(new DnsEndPoint("press.internal", 5432), CancellationToken.None);
        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    private sealed class Capture : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void A_string_instead_of_an_array_allows_nothing_and_warns()
    {
        var log = new Capture();
        var hosts = OutboundAddressGuard.AllowedHostsFrom(Config(("Webhooks:AllowedHosts", "press.internal")), log);
        (hosts ?? []).Should().BeEmpty();
        log.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public void An_array_binds_without_a_warning()
    {
        var log = new Capture();
        var hosts = OutboundAddressGuard.AllowedHostsFrom(Config(("Webhooks:AllowedHosts:0", "press.internal")), log);
        hosts.Should().Equal("press.internal");
        log.Levels.Should().BeEmpty();
    }

    [Fact]
    public void No_setting_is_silent()
    {
        var log = new Capture();
        OutboundAddressGuard.AllowedHostsFrom(Config(), log);
        log.Levels.Should().BeEmpty();
    }
}
