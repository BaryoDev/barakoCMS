using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Changes;
using barakoCMS.Infrastructure.Realtime;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static BarakoCMS.Tests.Realtime.ContentTopicKit;

namespace BarakoCMS.Tests.Realtime;

[Collection("Integration3")]
public class ContentChangeFeedTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHighWaterMark HighWater => host.Services.GetRequiredService<IHighWaterMark>();

    private async Task WaitForHighWaterAsync(long sequence)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await HighWater.CurrentAsync(Ct) < sequence)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"High-water mark never reached {sequence}.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>The newest event sequence written, once the high-water mark has reached it.</summary>
    private async Task<long> SettledAsync()
    {
        await using var session = host.OpenSession();
        var last = (await session.Events.QueryAllRawEvents().OrderByDescending(e => e.Sequence).Take(1).ToListAsync(Ct))
            .Select(e => e.Sequence).FirstOrDefault();
        await WaitForHighWaterAsync(last);
        return last;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        HttpClient client, string topic, long after, int? limit = null, Guid? branch = null)
    {
        var url = $"/api/changes?after={after}&topics={topic}" + (limit is null ? "" : $"&limit={limit}");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<ChangesResponse> ReadAsync(
        HttpClient client, string topic, long after, int? limit = null, Guid? branch = null)
    {
        var (status, body) = await GetAsync(client, topic, after, limit, branch);
        status.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<ChangesResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static JsonElement Data(ChangeItem item) => (JsonElement)item.Data;

    [Fact]
    public async Task A_reader_gets_the_events_of_the_topic_type_only()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var order = await TypeAsync(host, null, name: "order" + Guid.NewGuid().ToString("n")[..6]);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, ticket, null, branch);
        var writer = await WriterAsync(host, branch);
        var start = await SettledAsync();

        var ticketId = await CreateAsync(writer.Client, ticket, branch);
        await CreateAsync(writer.Client, order, branch);
        await SettledAsync();

        var page = await ReadAsync(reader.Client, topic, start);

        page.Items.Should().ContainSingle();
        page.Items[0].StreamId.Should().Be(ticketId);
        page.Items[0].Topics.Should().Equal(topic);
        page.Items[0].Type.Should().Contain("created", Exactly.Once(), "it is the ContentCreated event");
    }

    [Fact]
    public async Task Items_carry_no_field_values()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, ticket, null, branch);
        var writer = await WriterAsync(host, branch);
        var start = await SettledAsync();

        await CreateAsync(writer.Client, ticket, branch, title: "secret-title");
        await SettledAsync();

        var (status, body) = await GetAsync(reader.Client, topic, start);
        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("secret-title");
        var page = await ReadAsync(reader.Client, topic, start);
        Data(page.Items[0]).GetProperty("lifecycleState").GetString().Should().Be("Waiting");
    }

    [Fact]
    public async Task The_state_in_a_later_item_is_the_current_state()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, ticket, null, branch);
        var writer = await WriterAsync(host, branch);
        var start = await SettledAsync();

        var id = await CreateAsync(writer.Client, ticket, branch);
        await TransitionAsync(writer.Client, id, "Call", branch);
        await SettledAsync();

        var page = await ReadAsync(reader.Client, topic, start);

        page.Items.Should().HaveCountGreaterThanOrEqualTo(2);
        page.Items.Select(i => i.Sequence).Should().BeInAscendingOrder();
        page.Items.Should().OnlyContain(i => Data(i).GetProperty("lifecycleState").GetString() == "Called");
    }

    [Fact]
    public async Task Rows_of_another_branch_are_left_out()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var one = await ReaderAsync(host, ticket, null, b1);
        var both = await ReaderAsync(host, ticket, null, b1, b2);
        var writer = await WriterAsync(host, b1, b2);
        var start = await SettledAsync();

        var first = await CreateAsync(writer.Client, ticket, b1);
        var second = await CreateAsync(writer.Client, ticket, b2);
        await SettledAsync();

        (await ReadAsync(one.Client, topic, start)).Items.Select(i => i.StreamId).Should().Equal(first);
        (await ReadAsync(both.Client, topic, start)).Items.Select(i => i.StreamId).Should().Equal(first, second);

        var current = await ReaderAsync(
            host, ticket, new Dictionary<string, object> { ["$branch"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_BRANCH" } }, b1, b2);
        (await ReadAsync(current.Client, topic, start, branch: b1)).Items.Select(i => i.StreamId).Should().Equal(first);
    }

    [Fact]
    public async Task A_read_rule_is_applied_per_entry()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(
            host, ticket, new Dictionary<string, object> { ["Counter"] = new Dictionary<string, object> { ["_eq"] = "1" } }, branch);
        var writer = await WriterAsync(host, branch);
        var start = await SettledAsync();

        var shown = await CreateAsync(writer.Client, ticket, branch, counter: "1");
        await CreateAsync(writer.Client, ticket, branch, counter: "2");
        await SettledAsync();

        (await ReadAsync(reader.Client, topic, start)).Items.Select(i => i.StreamId).Should().Equal(shown);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_type_is_403()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic, name: "ticket" + Guid.NewGuid().ToString("n")[..6]);
        var other = await TypeAsync(host, NewTopic());
        var stranger = await ReaderAsync(host, other, null, Guid.NewGuid());

        var (status, body) = await GetAsync(stranger.Client, topic, 0);

        status.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain($"This caller cannot read '{ticket}'.");
    }

    [Fact]
    public async Task A_content_topic_cannot_be_combined_with_another_topic_is_400()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var reader = await ReaderAsync(host, ticket, null, Guid.NewGuid());

        var response = await reader.Client.GetAsync($"/api/changes?after=0&topics={topic}&topics=probe.tickets", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("A content topic cannot be combined with other topics.");
    }

    [Fact]
    public async Task A_busy_other_type_does_not_hide_the_topic_events()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var order = await TypeAsync(host, null, branchScoped: false, name: "order" + Guid.NewGuid().ToString("n")[..6], lifecycle: false);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, ticket, null, branch);
        var writer = await WriterAsync(host, branch);
        var start = await SettledAsync();

        for (var i = 0; i < 30; i++) await CreateAsync(writer.Client, order, null);
        var ticketId = await CreateAsync(writer.Client, ticket, branch);
        await SettledAsync();

        // Two raw events a page and ten pages a call: the first call stops at the cap with nothing.
        var first = await ReadAsync(reader.Client, topic, start, limit: 2);
        first.Items.Should().BeEmpty();
        first.Next.Should().BeGreaterThan(start);

        var found = new List<ChangeItem>();
        var cursor = first.Next;
        for (var calls = 0; calls < 5 && found.Count == 0; calls++)
        {
            var page = await ReadAsync(reader.Client, topic, cursor, limit: 2);
            found.AddRange(page.Items);
            cursor = page.Next;
        }

        found.Select(i => i.StreamId).Should().Equal(ticketId);
    }

    [Fact]
    public async Task Nothing_past_the_high_water_mark_is_returned()
    {
        var topic = NewTopic();
        var ticket = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, ticket, null, branch);
        var writer = await WriterAsync(host, branch);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var start = await SettledAsync();

        // A writer takes a sequence and holds its transaction open.
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var slowId = Guid.NewGuid();
        await using (var slow = store.LightweightSession(Marten.Services.SessionOptions.ForTransaction(transaction)))
        {
            slow.Events.StartStream(slowId, new ProbeSecretNoted(slowId, "slow"));
            await slow.SaveChangesAsync(Ct);
        }

        // A ticket takes a higher sequence and commits first.
        var fastId = await CreateAsync(writer.Client, ticket, branch);

        // Longer than Marten's 3 second stale threshold.
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        var before = await ReadAsync(reader.Client, topic, start);
        before.Items.Select(i => i.StreamId).Should().NotContain(fastId, "the feed must not pass the open lower sequence");

        await transaction.CommitAsync(Ct);
        await SettledAsync();
        var after = await ReadAsync(reader.Client, topic, before.Next);

        after.Items.Select(i => i.StreamId).Should().Contain(fastId);
    }

    [Fact]
    public async Task Module_topics_are_unchanged()
    {
        var client = await host.ClientHoldingAsync(["read_probe_tickets"]);

        var response = await client.GetAsync("/api/changes?after=0&topics=probe.tickets&limit=1", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
