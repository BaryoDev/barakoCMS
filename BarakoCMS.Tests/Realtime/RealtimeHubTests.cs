using System.Net;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Realtime;
using BarakoCMS.Tests.Clinic;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

[Collection("Sequential")]
public class RealtimeHubTests(ClinicProbeHost host) : IClassFixture<ClinicProbeHost>
{
    private static readonly string[] Tickets = ["read_probe_tickets"];

    internal static async Task<HubConnection> ConnectAsync(ClinicProbeHost host, string token, string? tenant = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.CreateClient().BaseAddress!, RealtimeHub.Path), o =>
            {
                // Long polling is plain HTTP, which the in-memory test server serves. WebSockets
                // would need a real socket.
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => host.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                if (tenant is not null) o.Headers[TenantResolutionMiddleware.TenantHeader] = tenant;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task Hub_HeldTopic_SubscribeSucceeds()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var connection = await ConnectAsync(host, caller.Token);

        var act = () => connection.InvokeAsync("Subscribe", "probe.tickets");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Hub_TopicNotHeld_SubscribeRefused()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var connection = await ConnectAsync(host, caller.Token);

        var act = () => connection.InvokeAsync("Subscribe", "probe.secrets");

        await act.Should().ThrowAsync<HubException>().WithMessage("*read_probe_secrets*");
    }

    [Fact]
    public async Task Hub_UnknownTopic_SubscribeRefused()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var connection = await ConnectAsync(host, caller.Token);

        var act = () => connection.InvokeAsync("Subscribe", "no.such");

        await act.Should().ThrowAsync<HubException>().WithMessage("*Unknown topic 'no.such'*");
    }

    [Fact]
    public async Task Hub_Anonymous_NegotiateReturns401()
    {
        var response = await host.CreateClient().PostAsync($"{RealtimeHub.Path}/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Hub_QueryStringToken_NegotiateAccepted()
    {
        // Browsers and some socket clients cannot set headers on a WebSocket, so SignalR sends the
        // token as access_token in the query string. Only hub paths read it there.
        var caller = await host.CallerHoldingAsync(Tickets);

        var response = await host.CreateClient().PostAsync(
            $"{RealtimeHub.Path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(caller.Token)}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_QueryStringToken_IsIgnoredOutsideHubs()
    {
        var caller = await host.CallerHoldingAsync(Tickets);

        var response = await host.CreateClient().GetAsync(
            $"/api/changes?after=0&topics=probe.tickets&access_token={Uri.EscapeDataString(caller.Token)}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
