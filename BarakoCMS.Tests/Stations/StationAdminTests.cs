using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Stations;
using barakoCMS.Infrastructure.Auth.Stations;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Xunit;

namespace BarakoCMS.Tests.Stations;

[Collection("Integration2")]
public class StationAdminTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static readonly string[] Manager = ["manage_stations", "read_probe_tickets"];

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, params string[] capabilities) =>
        client.PostAsJsonAsync("/api/stations", new CreateStationRequest { Name = "Lobby TV", Capabilities = capabilities.ToList() });

    [Fact]
    public async Task Station_Create_ReturnsKeyOnceAndListOmitsIt()
    {
        var client = await host.ClientHoldingAsync(Manager);

        var response = await CreateAsync(client, "read_probe_tickets");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<CreatedStation>())!;
        created.Key.Should().StartWith("bsta_").And.HaveLength(5 + 43);
        created.DisplayPrefix.Should().Be(created.Key[..13]);

        var listed = await client.GetStringAsync("/api/stations");
        listed.Should().Contain(created.Id.ToString()).And.NotContain(created.Key);
    }

    [Fact]
    public async Task Station_Create_StoresOnlyTheKeyHash()
    {
        var client = await host.ClientHoldingAsync(Manager);
        var created = (await (await CreateAsync(client, "read_probe_tickets")).Content.ReadFromJsonAsync<CreatedStation>())!;

        await using var session = host.OpenSession();
        var stored = await session.LoadAsync<Station>(created.Id);

        stored!.KeyHash.Should().Be(StationKeys.Hash(created.Key));
        stored.KeyHash.Should().NotContain(created.Key);
    }

    [Fact]
    public async Task Station_CapabilityCreatorLacks_Returns400()
    {
        var client = await host.ClientHoldingAsync(Manager);

        var response = await CreateAsync(client, "read_probe_secrets");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("read_probe_secrets");
    }

    [Fact]
    public async Task Station_Wildcard_Returns400()
    {
        var client = await host.CreateAdminClientAsync();

        (await CreateAsync(client, "*")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Station_UnknownCapability_Returns400()
    {
        var client = await host.CreateAdminClientAsync();

        (await CreateAsync(client, "no_such_capability")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Station_NoCapabilities_Returns400()
    {
        var client = await host.ClientHoldingAsync(Manager);

        (await CreateAsync(client)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Station_WithoutManageStations_Returns403()
    {
        var client = await host.ClientHoldingAsync(["read_probe_tickets"]);

        (await CreateAsync(client, "read_probe_tickets")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Station_OtherTenantsStation_NotListedAndNotRevocable()
    {
        var tenant = await host.CreateTenantAsync();
        var theirs = (await (await CreateAsync(await host.ClientHoldingAsync(Manager, tenant), "read_probe_tickets"))
            .Content.ReadFromJsonAsync<CreatedStation>())!;
        var mine = await host.ClientHoldingAsync(Manager);

        (await mine.GetStringAsync("/api/stations")).Should().NotContain(theirs.Id.ToString());
        (await mine.PostAsync($"/api/stations/{theirs.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Station_Revoke_ShowsRevokedInList()
    {
        var client = await host.ClientHoldingAsync(Manager);
        var created = (await (await CreateAsync(client, "read_probe_tickets")).Content.ReadFromJsonAsync<CreatedStation>())!;

        (await client.PostAsync($"/api/stations/{created.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = (await client.GetFromJsonAsync<List<StationSummary>>("/api/stations"))!;
        list.Single(s => s.Id == created.Id).Revoked.Should().BeTrue();
    }

    private async Task<List<AuditEvent>> AuditAsync(string action, Guid stationId)
    {
        await using var session = host.OpenSession();
        return (await session.Query<AuditEvent>().Where(e => e.Action == action && e.TargetId == stationId.ToString()).ToListAsync()).ToList();
    }

    [Fact]
    public async Task Station_Revoke_SetsRevokedAt()
    {
        var client = await host.ClientHoldingAsync(Manager);
        var created = (await (await CreateAsync(client, "read_probe_tickets")).Content.ReadFromJsonAsync<CreatedStation>())!;

        await client.PostAsync($"/api/stations/{created.Id}/revoke", null);

        await using var session = host.OpenSession();
        (await session.LoadAsync<Station>(created.Id))!.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Station_RevokeTwice_KeepsFirstRevokedAtAndAuditsOnce()
    {
        var client = await host.ClientHoldingAsync(Manager);
        var created = (await (await CreateAsync(client, "read_probe_tickets")).Content.ReadFromJsonAsync<CreatedStation>())!;

        await client.PostAsync($"/api/stations/{created.Id}/revoke", null);
        DateTime? first;
        await using (var session = host.OpenSession())
            first = (await session.LoadAsync<Station>(created.Id))!.RevokedAt;

        (await client.PostAsync($"/api/stations/{created.Id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var again = host.OpenSession();
        (await again.LoadAsync<Station>(created.Id))!.RevokedAt.Should().Be(first);
        (await AuditAsync("station.revoked", created.Id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Station_Create_AuditsWithoutThePlainKey()
    {
        var client = await host.ClientHoldingAsync(Manager);
        var created = (await (await CreateAsync(client, "read_probe_tickets")).Content.ReadFromJsonAsync<CreatedStation>())!;

        var rows = await AuditAsync("station.created", created.Id);

        rows.Should().HaveCount(1);
        System.Text.Json.JsonSerializer.Serialize(rows[0]).Should().NotContain(created.Key);
    }

    [Fact]
    public async Task Station_CapabilityInOtherCase_StoredCanonical()
    {
        var client = await host.ClientHoldingAsync(Manager);

        var response = await CreateAsync(client, "READ_PROBE_TICKETS", "read_probe_tickets");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<CreatedStation>())!;
        created.Capabilities.Should().Equal("read_probe_tickets");
        var list = (await client.GetFromJsonAsync<List<StationSummary>>("/api/stations"))!;
        list.Single(s => s.Id == created.Id).Capabilities.Should().Equal("read_probe_tickets");
    }

    [Fact]
    public async Task Station_ManageStations_Returns400()
    {
        var client = await host.ClientHoldingAsync(Manager);

        var response = await CreateAsync(client, "manage_stations");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("manage_stations");
    }

    [Fact]
    public async Task Station_OrdinaryCapabilityHeldByAPlatformCaller_Returns200()
    {
        var client = await host.ClientHoldingAsync(["manage_stations", "manage_tenants", "read_probe_tickets"]);

        var response = await CreateAsync(client, "read_probe_tickets");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("manage_tenants")]
    [InlineData("manage_roles")]
    [InlineData("manage_platform_roles")]
    [InlineData("manage_users")]
    [InlineData("manage_email_settings")]
    [InlineData("view_hidden")]
    public async Task Station_PlatformCapabilityTheCallerHolds_Returns400AndStoresNothing(string platformCapability)
    {
        var client = await host.ClientHoldingAsync(["manage_stations", "read_probe_tickets", platformCapability]);
        var name = $"Platform {platformCapability}";

        var response = await client.PostAsJsonAsync("/api/stations",
            new CreateStationRequest { Name = name, Capabilities = ["read_probe_tickets", platformCapability] });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(platformCapability);

        var listed = await client.GetStringAsync("/api/stations");
        listed.Should().NotContain(name);
    }

    [Fact]
    public async Task Station_PlatformCapabilityInDifferentCasing_Returns400()
    {
        var client = await host.ClientHoldingAsync(["manage_stations", "manage_tenants"]);

        var response = await CreateAsync(client, "MANAGE_TENANTS");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("manage_tenants");
    }
}
