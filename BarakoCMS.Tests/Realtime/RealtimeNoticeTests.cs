using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Changes;
using barakoCMS.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

[Collection("Sequential")]
public class RealtimeNoticeTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static readonly string[] Tickets = ["read_probe_tickets"];

    private sealed class Listening(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _notices = Channel.CreateUnbounded<JsonElement>();

        public HubConnection Connection => connection;

        public static async Task<Listening> StartAsync(SampleProbeHost host, string token, string topic, string? tenant = null)
        {
            var connection = await RealtimeHubTests.ConnectAsync(host, token, tenant);
            var listening = new Listening(connection);
            connection.On<JsonElement>("changed", n => listening._notices.Writer.TryWrite(n));
            await connection.InvokeAsync("Subscribe", topic);
            return listening;
        }

        /// <summary>Every notice up to and including the first one for <paramref name="streamId"/>.</summary>
        public async Task<List<JsonElement>> UntilStreamAsync(Guid streamId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var seen = new List<JsonElement>();
            while (true)
            {
                var notice = await _notices.Reader.ReadAsync(timeout.Token);
                seen.Add(notice);
                if (notice.GetProperty("streamId").GetGuid() == streamId) return seen;
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    [Fact]
    public async Task Hub_SubscribedTopic_ReceivesNoticeAfterCommit()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        var id = Guid.NewGuid();

        var sequence = await host.AppendAsync(new ProbeTicketIssued(id, "N-1"), id);

        var notice = (await listening.UntilStreamAsync(id)).Last();
        notice.GetProperty("topic").GetString().Should().Be("probe.tickets");
        notice.GetProperty("sequence").GetInt64().Should().Be(sequence);
        notice.GetProperty("type").GetString().Should().Be("sample-probe.probe_ticket_issued");
    }

    [Fact]
    public async Task Hub_NoticeThenFeedPolling_ReachesTheAnnouncedSequence()
    {
        // The notice goes out at commit, the feed stops at the high-water mark, which the daemon
        // advances later. A client that reads once on the notice may see next below the sequence, so
        // it reads again after a short delay until next reaches it. This pins that contract.
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        var start = await host.Services.GetRequiredService<IHighWaterMark>().CurrentAsync();
        var id = Guid.NewGuid();

        await host.AppendAsync(new ProbeTicketIssued(id, "POLL-1"), id);

        var notice = (await listening.UntilStreamAsync(id)).Last();
        var sequence = notice.GetProperty("sequence").GetInt64();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var delay = TimeSpan.FromMilliseconds(50);
        ChangesResponse page;
        while (true)
        {
            var response = await caller.Client.GetAsync($"/api/changes?after={start}&topics=probe.tickets");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            page = (await response.Content.ReadFromJsonAsync<ChangesResponse>())!;
            if (page.Next >= sequence) break;
            DateTime.UtcNow.Should().BeBefore(deadline, "the feed must reach the announced sequence");
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
        }

        page.Items.Should().Contain(i => i.StreamId == id && i.Sequence == sequence);
    }

    [Fact]
    public async Task Hub_Notice_CarriesNoEventData()
    {
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        var id = Guid.NewGuid();

        await host.AppendAsync(new ProbeTicketIssued(id, "LABEL-THAT-MUST-NOT-LEAK"), id);

        var notice = (await listening.UntilStreamAsync(id)).Last();
        notice.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("topic", "sequence", "streamId", "type");
        notice.GetRawText().Should().NotContain("LABEL-THAT-MUST-NOT-LEAK");
    }

    [Fact]
    public async Task Hub_OtherTenantsCommit_SendsNoNotice()
    {
        var tenant = await host.CreateTenantAsync();
        var caller = await host.CallerHoldingAsync(Tickets);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        var theirs = Guid.NewGuid();
        var mine = Guid.NewGuid();

        // Theirs first. Notices on one connection arrive in send order, so by the time ours
        // arrives, theirs would already be here if it had been sent.
        await host.AppendAsync(new ProbeTicketIssued(theirs, "THEIRS"), theirs, tenant);
        await host.AppendAsync(new ProbeTicketIssued(mine, "MINE"), mine);

        var seen = await listening.UntilStreamAsync(mine);
        seen.Select(n => n.GetProperty("streamId").GetGuid()).Should().NotContain(theirs);
    }

    [Fact]
    public async Task Hub_EventOutsideSubscribedTopic_SendsNoNotice()
    {
        var caller = await host.CallerHoldingAsync(["read_probe_tickets", "read_probe_secrets"]);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        var secret = Guid.NewGuid();
        var ticket = Guid.NewGuid();

        await host.AppendAsync(new ProbeSecretNoted(secret, "S"), secret);
        await host.AppendAsync(new ProbeTicketIssued(ticket, "T"), ticket);

        var seen = await listening.UntilStreamAsync(ticket);
        seen.Select(n => n.GetProperty("streamId").GetGuid()).Should().NotContain(secret);
    }

    [Fact]
    public async Task Hub_AfterUnsubscribe_SendsNoNotice()
    {
        var caller = await host.CallerHoldingAsync(["read_probe_tickets", "read_probe_secrets"]);
        await using var listening = await Listening.StartAsync(host, caller.Token, "probe.tickets");
        await listening.Connection.InvokeAsync("Subscribe", "probe.secrets");
        await listening.Connection.InvokeAsync("Unsubscribe", "probe.tickets");
        var ticket = Guid.NewGuid();
        var secret = Guid.NewGuid();

        await host.AppendAsync(new ProbeTicketIssued(ticket, "T"), ticket);
        await host.AppendAsync(new ProbeSecretNoted(secret, "S"), secret);

        var seen = await listening.UntilStreamAsync(secret);
        seen.Select(n => n.GetProperty("streamId").GetGuid()).Should().NotContain(ticket);
    }
}
