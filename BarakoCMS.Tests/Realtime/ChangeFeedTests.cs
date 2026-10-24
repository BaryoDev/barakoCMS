using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Changes;
using barakoCMS.Infrastructure.Realtime;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

[Collection("Integration1")]
public class ChangeFeedTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static readonly string[] Tickets = ["read_probe_tickets"];

    private IHighWaterMark HighWater => host.Services.GetRequiredService<IHighWaterMark>();

    private async Task WaitForHighWaterAsync(long sequence)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await HighWater.CurrentAsync() < sequence)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"High-water mark never reached {sequence}.");
            await Task.Delay(100);
        }
    }

    private static async Task<ChangesResponse> ReadAsync(HttpClient client, long after, string topic = "probe.tickets", int? limit = null)
    {
        var url = $"/api/changes?after={after}&topics={topic}" + (limit is null ? "" : $"&limit={limit}");
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChangesResponse>())!;
    }

    [Fact]
    public async Task Feed_HeldTopic_ReturnsOnlyThatTopicsEventsWithAliasAndData()
    {
        var start = await HighWater.CurrentAsync();
        var id = Guid.NewGuid();
        await host.AppendAsync(new ProbeTicketIssued(id, "A-100"), id);
        await host.AppendAsync(new ProbeSecretNoted(Guid.NewGuid(), "hidden"), Guid.NewGuid());
        var last = await host.AppendAsync(new ProbeTicketCalled(id, "Counter 1"), id);
        await WaitForHighWaterAsync(last);
        var client = await host.ClientHoldingAsync(Tickets);

        var page = await ReadAsync(client, start);

        page.Items.Select(i => i.Type).Should().Equal("sample-probe.probe_ticket_issued", "sample-probe.probe_ticket_called");
        page.Items.Should().OnlyContain(i => i.StreamId == id && i.Topics.SequenceEqual(new[] { "probe.tickets" }));
        ((JsonElement)page.Items[0].Data).GetProperty("label").GetString().Should().Be("A-100");
        page.Next.Should().BeGreaterThanOrEqualTo(last);
    }

    [Fact]
    public async Task Feed_UnknownTopic_Returns400()
    {
        var client = await host.ClientHoldingAsync(Tickets);

        var response = await client.GetAsync("/api/changes?after=0&topics=no.such");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Feed_NoTopic_Returns400()
    {
        var client = await host.ClientHoldingAsync(Tickets);

        (await client.GetAsync("/api/changes?after=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Feed_TopicNotHeld_Returns403()
    {
        var client = await host.ClientHoldingAsync(Tickets);

        var response = await client.GetAsync("/api/changes?after=0&topics=probe.tickets&topics=probe.secrets");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("read_probe_secrets");
    }

    [Fact]
    public async Task Feed_Anonymous_Returns401()
    {
        (await host.CreateClient().GetAsync("/api/changes?after=0&topics=probe.tickets"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Feed_Paging_NextResumesWithoutLossOrRepeat()
    {
        var start = await HighWater.CurrentAsync();
        long last = 0;
        for (var i = 0; i < 5; i++)
            last = await host.AppendAsync(new ProbeTicketIssued(Guid.NewGuid(), $"P-{i}"), Guid.NewGuid());
        await WaitForHighWaterAsync(last);
        var client = await host.ClientHoldingAsync(Tickets);

        var seen = new List<long>();
        var after = start;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await ReadAsync(client, after, limit: 2);
            seen.AddRange(page.Items.Select(i => i.Sequence));
            if (page.Items.Count == 0) break;
            after = page.Next;
        }

        seen.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
        seen.Should().HaveCountGreaterThanOrEqualTo(5).And.Contain(last);
    }

    [Fact]
    public async Task Feed_LimitAbove1000_IsCapped()
    {
        var client = await host.ClientHoldingAsync(Tickets);

        var page = await ReadAsync(client, 0, limit: 50_000);

        page.Items.Count.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Feed_NegativeAfter_Returns400()
    {
        var client = await host.ClientHoldingAsync(Tickets);

        (await client.GetAsync("/api/changes?after=-1&topics=probe.tickets")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Feed_OtherTenantsEvents_AreNotReturned()
    {
        var tenant = await host.CreateTenantAsync();
        var start = await HighWater.CurrentAsync();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await host.AppendAsync(new ProbeTicketIssued(mine, "MINE"), mine);
        var last = await host.AppendAsync(new ProbeTicketIssued(theirs, "THEIRS"), theirs, tenant);
        await WaitForHighWaterAsync(last);

        var defaultPage = await ReadAsync(await host.ClientHoldingAsync(Tickets), start);
        var tenantPage = await ReadAsync(await host.ClientHoldingAsync(Tickets, tenant), start);

        defaultPage.Items.Select(i => i.StreamId).Should().Contain(mine).And.NotContain(theirs);
        tenantPage.Items.Select(i => i.StreamId).Should().Contain(theirs).And.NotContain(mine);
    }

    [Fact]
    public async Task Feed_CapabilityRevoked_RefusedOnNextRequest()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        (await caller.Client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await host.CreateAdminClientAsync();
        var update = await admin.PutAsJsonAsync($"/api/roles/{caller.RoleId}", new
        {
            Id = caller.RoleId,
            Name = caller.RoleName,
            Description = "",
            Permissions = Array.Empty<object>(),
            SystemCapabilities = Array.Empty<string>(),
        });
        update.IsSuccessStatusCode.Should().BeTrue(await update.Content.ReadAsStringAsync());

        (await caller.Client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Feed_LateCommitOfLowerSequence_IsNotSkipped()
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await WaitForHighWaterAsync(await host.AppendAsync(new ProbeSecretNoted(Guid.NewGuid(), "settle"), Guid.NewGuid()));
        var start = await HighWater.CurrentAsync();

        // A writer takes a sequence and holds its transaction open.
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var slowId = Guid.NewGuid();
        await using (var slow = store.LightweightSession(Marten.Services.SessionOptions.ForTransaction(transaction)))
        {
            slow.Events.StartStream(slowId, new ProbeTicketIssued(slowId, "SLOW"));
            await slow.SaveChangesAsync();
        }

        // A second writer takes a higher sequence and commits first.
        var fastId = Guid.NewGuid();
        var fast = await host.AppendAsync(new ProbeTicketIssued(fastId, "FAST"), fastId);

        // Longer than Marten's 3 second stale threshold, so a gap skip would have happened by now.
        await Task.Delay(TimeSpan.FromSeconds(5));
        var client = await host.ClientHoldingAsync(Tickets);
        var before = await ReadAsync(client, start);

        before.Items.Select(i => i.StreamId).Should().NotContain(fastId, "the feed must not pass the open lower sequence");
        before.Next.Should().BeLessThan(fast);

        await transaction.CommitAsync();
        await WaitForHighWaterAsync(fast);
        var after = await ReadAsync(client, before.Next);

        after.Items.Select(i => i.StreamId).Should().ContainInOrder(slowId, fastId);
    }
}
