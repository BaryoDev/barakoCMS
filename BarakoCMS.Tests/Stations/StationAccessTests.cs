using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Features.Changes;
using barakoCMS.Features.Stations;
using barakoCMS.Infrastructure.Auth.Stations;
using barakoCMS.Infrastructure.Realtime;
using BarakoCMS.Tests.SampleProbe;
using BarakoCMS.Tests.Realtime;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Stations;

[Collection("Integration1")]
public class StationAccessTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private async Task<CreatedStation> CreateStationAsync(string? tenant = null, params string[] capabilities)
    {
        var manager = (await host.CallerHoldingAsync(["manage_stations", "read_probe_tickets", "read_probe_secrets"], tenant)).Client;
        var response = await manager.PostAsJsonAsync("/api/stations", new CreateStationRequest
        {
            Name = "Board",
            Capabilities = (capabilities.Length == 0 ? ["read_probe_tickets"] : capabilities).ToList(),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedStation>())!;
    }

    private HttpClient StationClient(string key)
    {
        // No X-Tenant on purpose: a station's tenant comes from its key.
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task WaitForHighWaterAsync(long sequence)
    {
        var highWater = host.Services.GetRequiredService<IHighWaterMark>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await highWater.CurrentAsync() < sequence)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"High-water mark never reached {sequence}.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Station_HeldTopic_ReadsFeed()
    {
        var station = await CreateStationAsync();
        var id = Guid.NewGuid();
        var sequence = await host.AppendAsync(new ProbeTicketIssued(id, "S-1"), id);
        await WaitForHighWaterAsync(sequence);

        var page = await StationClient(station.Key).GetFromJsonAsync<ChangesResponse>("/api/changes?after=0&topics=probe.tickets&limit=1000");

        page!.Items.Select(i => i.StreamId).Should().Contain(id);
    }

    [Fact]
    public async Task Station_TopicNotGranted_Returns403()
    {
        var station = await CreateStationAsync();

        var response = await StationClient(station.Key).GetAsync("/api/changes?after=0&topics=probe.secrets");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Station_EndpointNotOpenToStations_Returns403()
    {
        var station = await CreateStationAsync();

        (await StationClient(station.Key).GetAsync("/api/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StationClient(station.Key).GetAsync("/api/stations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Station_Revoked_RefusedOnNextRequest()
    {
        var station = await CreateStationAsync();
        var client = StationClient(station.Key);
        (await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        var manager = (await host.CallerHoldingAsync(["manage_stations"])).Client;
        (await manager.PostAsync($"/api/stations/{station.Id}/revoke", null)).EnsureSuccessStatusCode();

        (await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Station_WrongKey_Returns401()
    {
        var station = await CreateStationAsync();

        var response = await StationClient(station.Key + "x").GetAsync("/api/changes?after=0&topics=probe.tickets");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Station_OtherTenantHeader_StillReadsOwnTenant()
    {
        var tenant = await host.CreateTenantAsync();
        var station = await CreateStationAsync(tenant);
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        await host.AppendAsync(new ProbeTicketIssued(other, "DEFAULT"), other);
        var sequence = await host.AppendAsync(new ProbeTicketIssued(mine, "MINE"), mine, tenant);
        await WaitForHighWaterAsync(sequence);
        var client = StationClient(station.Key);
        client.DefaultRequestHeaders.Add("X-Tenant", "default");

        var response = await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1000");

        // The handler sets the tenant from the station document after resolution read the header,
        // so the request is served from the station's own tenant.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<ChangesResponse>())!;
        page.Items.Select(i => i.StreamId).Should().Contain(mine).And.NotContain(other);
    }

    [Theory]
    [InlineData("bsta_")]
    [InlineData("bsta_not-a-real-key")]
    [InlineData("bsta_   ")]
    public async Task Station_EmptyOrMalformedKey_Returns401(string key)
    {
        var response = await StationClient(key).GetAsync("/api/changes?after=0&topics=probe.tickets");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Station_TenantInactive_Returns401()
    {
        var tenant = await host.CreateTenantAsync();
        var station = await CreateStationAsync(tenant);
        var client = StationClient(station.Key);
        (await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var session = host.OpenSession())
        {
            var row = await session.Query<barakoCMS.Models.Tenant>().FirstAsync(t => t.Slug == tenant, TestContext.Current.CancellationToken);
            row.Status = barakoCMS.Models.TenantStatus.Suspended;
            session.Store(row);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Station_Ticket_ExpiresAboutFifteenMinutesAfterIssue()
    {
        var station = await CreateStationAsync();
        using var scope = host.Services.CreateScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers.Authorization = $"Bearer {station.Key}";

        var result = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions
            .AuthenticateAsync(http, StationAuthenticationHandler.SchemeName);

        result.Succeeded.Should().BeTrue();
        var props = result.Properties!;
        (props.ExpiresUtc!.Value - props.IssuedUtc!.Value).Should().Be(TimeSpan.FromMinutes(15));
        props.ExpiresUtc.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Station_Hub_SubscribesHeldTopicAndRefusesOther()
    {
        var station = await CreateStationAsync();
        await using var connection = await RealtimeHubTests.ConnectAsync(host, station.Key);

        await connection.InvokeAsync("Subscribe", "probe.tickets");
        var other = () => connection.InvokeAsync("Subscribe", "probe.secrets");

        await other.Should().ThrowAsync<HubException>().WithMessage("*read_probe_secrets*");
    }

    [Fact]
    public async Task Station_HubQueryStringKey_NegotiateAccepted()
    {
        var station = await CreateStationAsync();

        var response = await host.CreateClient().PostAsync(
            $"{RealtimeHub.Path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(station.Key)}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Station_Hub_RevokedOnOpenConnection_SubscribeRefused()
    {
        var station = await CreateStationAsync();
        // A WebSocket authenticates once, when it opens. Long polling would authenticate every
        // poll and cut a revoked key off at the transport before the hub method ran.
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.CreateClient().BaseAddress!, RealtimeHub.Path), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = false;
                o.HttpMessageHandlerFactory = _ => host.CreateHandler();
                o.WebSocketFactory = async (context, ct) => await ((Microsoft.AspNetCore.TestHost.TestServer)host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateWebSocketClient().ConnectAsync(
                    new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(station.Key)}"), ct);
                o.AccessTokenProvider = () => Task.FromResult<string?>(station.Key);
            })
            .Build();
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", "probe.tickets");

        var manager = (await host.CallerHoldingAsync(["manage_stations"])).Client;
        (await manager.PostAsync($"/api/stations/{station.Id}/revoke", null)).EnsureSuccessStatusCode();

        var act = () => connection.InvokeAsync("Subscribe", "probe.tickets");

        await act.Should().ThrowAsync<HubException>().WithMessage("*revoked*");
    }

    [Fact]
    public async Task Station_Hub_RevokedKey_NegotiateReturns401()
    {
        var station = await CreateStationAsync();
        var manager = (await host.CallerHoldingAsync(["manage_stations"])).Client;
        (await manager.PostAsync($"/api/stations/{station.Id}/revoke", null)).EnsureSuccessStatusCode();

        var response = await host.CreateClient().PostAsync(
            $"{RealtimeHub.Path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(station.Key)}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Station_QueryStringKey_IsIgnoredOutsideHubs()
    {
        var station = await CreateStationAsync();

        var response = await host.CreateClient().GetAsync(
            $"/api/changes?after=0&topics=probe.tickets&access_token={Uri.EscapeDataString(station.Key)}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
