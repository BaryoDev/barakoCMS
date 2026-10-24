using barakoCMS.Infrastructure.Realtime;
using System.Net.Http.Json;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using static BarakoCMS.Tests.Realtime.ContentTopicKit;

namespace BarakoCMS.Tests.Realtime;

[Collection("Integration1")]
public class ContentRealtimeHubTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Silent = TimeSpan.FromSeconds(3);

    private static (HubConnection Connection, Func<TimeSpan, Task<RealtimeNotice?>> Next) Listen(HubConnection connection)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<RealtimeNotice>();
        connection.On<RealtimeNotice>("changed", n => channel.Writer.TryWrite(n));

        async Task<RealtimeNotice?> Next(TimeSpan wait)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(wait);
            try
            {
                return await channel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                return null;
            }
        }

        return (connection, Next);
    }

    [Fact]
    public async Task A_reader_can_subscribe_to_a_content_topic()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var reader = await ReaderAsync(host, type, null, Guid.NewGuid());
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);

        var act = () => connection.InvokeAsync("Subscribe", topic, Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_type_is_refused()
    {
        var topic = NewTopic();
        await TypeAsync(host, topic, name: "ticket" + Guid.NewGuid().ToString("n")[..6]);
        var other = await TypeAsync(host, NewTopic());
        var stranger = await ReaderAsync(host, other, null, Guid.NewGuid());
        await using var connection = await RealtimeHubTests.ConnectAsync(host, stranger.Token);

        var act = () => connection.InvokeAsync("Subscribe", topic, Ct);

        await act.Should().ThrowAsync<HubException>().WithMessage("*cannot read 'ticket*");
    }

    [Fact]
    public async Task An_unknown_topic_still_says_unknown()
    {
        var type = await TypeAsync(host, NewTopic());
        var reader = await ReaderAsync(host, type, null, Guid.NewGuid());
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);

        var act = () => connection.InvokeAsync("Subscribe", "no.such.topic", Ct);

        await act.Should().ThrowAsync<HubException>().WithMessage("*Unknown topic 'no.such.topic'*");
    }

    [Fact]
    public async Task A_notice_arrives_for_the_callers_branch()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, type, null, branch);
        var writer = await WriterAsync(host, branch);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        var (_, next) = Listen(connection);
        await connection.InvokeAsync("Subscribe", topic, Ct);

        var id = await CreateAsync(writer.Client, type, branch);

        var notice = await next(Arrives);
        notice.Should().NotBeNull();
        notice!.Topic.Should().Be(topic);
        notice.StreamId.Should().Be(id);
    }

    [Fact]
    public async Task A_notice_arrives_for_each_entry_of_a_batch_create()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic, branchScoped: false);
        var reader = await ReaderAsync(host, type, null);
        var writer = await WriterAsync(host);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        var (_, next) = Listen(connection);
        await connection.InvokeAsync("Subscribe", topic, Ct);
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };

        var response = await writer.Client.PostAsJsonAsync("/api/contents/batch", new
        {
            steps = ids.Select(id => new
            {
                op = "create",
                contentType = type,
                id,
                data = new Dictionary<string, object> { ["Title"] = "B-1", ["Counter"] = "1" },
            }),
        }, Ct);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotBeNull();
        response.IsSuccessStatusCode.Should().BeTrue();

        var heard = new[] { (await next(Arrives))?.StreamId, (await next(Arrives))?.StreamId };
        heard.Should().BeEquivalentTo(ids.Select(i => (Guid?)i));
    }

    [Fact]
    public async Task No_notice_arrives_for_another_branch()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var reader = await ReaderAsync(host, type, null, b1);
        var writer = await WriterAsync(host, b1, b2);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        var (_, next) = Listen(connection);
        await connection.InvokeAsync("Subscribe", topic, Ct);

        await CreateAsync(writer.Client, type, b2);
        (await next(Silent)).Should().BeNull("the entry belongs to a branch the reader does not hold");

        var mine = await CreateAsync(writer.Client, type, b1);
        (await next(Arrives))!.StreamId.Should().Be(mine);
    }

    [Fact]
    public async Task A_user_in_two_branches_hears_both()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var reader = await ReaderAsync(host, type, null, b1, b2);
        var writer = await WriterAsync(host, b1, b2);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        var (_, next) = Listen(connection);
        await connection.InvokeAsync("Subscribe", topic, Ct);

        var first = await CreateAsync(writer.Client, type, b1);
        var second = await CreateAsync(writer.Client, type, b2);

        var heard = new[] { (await next(Arrives))?.StreamId, (await next(Arrives))?.StreamId };
        heard.Should().BeEquivalentTo(new Guid?[] { first, second });
    }

    [Fact]
    public async Task A_caller_with_no_branch_cannot_subscribe_to_a_branch_scoped_topic()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var reader = await ReaderAsync(host, type);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);

        var act = () => connection.InvokeAsync("Subscribe", topic, Ct);

        await act.Should().ThrowAsync<HubException>().WithMessage("*holds no branch*");
    }

    [Fact]
    public async Task A_type_that_is_not_branch_scoped_notifies_every_subscriber()
    {
        var topic = NewTopic("board");
        var type = await TypeAsync(host, topic, branchScoped: false, name: "notice" + Guid.NewGuid().ToString("n")[..6]);
        var reader = await ReaderAsync(host, type);
        var other = await ReaderAsync(host, type, null, Guid.NewGuid());
        var writer = await WriterAsync(host);
        await using var first = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        await using var second = await RealtimeHubTests.ConnectAsync(host, other.Token);
        var (_, nextFirst) = Listen(first);
        var (_, nextSecond) = Listen(second);
        await first.InvokeAsync("Subscribe", topic, Ct);
        await second.InvokeAsync("Subscribe", topic, Ct);

        var id = await CreateAsync(writer.Client, type, null);

        (await nextFirst(Arrives))!.StreamId.Should().Be(id);
        (await nextSecond(Arrives))!.StreamId.Should().Be(id);
    }

    [Fact]
    public async Task A_notice_names_no_field_values()
    {
        var topic = NewTopic();
        var type = await TypeAsync(host, topic);
        var branch = Guid.NewGuid();
        var reader = await ReaderAsync(host, type, null, branch);
        var writer = await WriterAsync(host, branch);
        var raw = new TaskCompletionSource<string>();
        await using var connection = await RealtimeHubTests.ConnectAsync(host, reader.Token);
        connection.On<System.Text.Json.JsonElement>("changed", e => raw.TrySetResult(e.GetRawText()));
        await connection.InvokeAsync("Subscribe", topic, Ct);

        await CreateAsync(writer.Client, type, branch, title: "Secret-Title-Value", counter: "Secret-Counter-Value");

        var text = await raw.Task.WaitAsync(Arrives, Ct);
        text.Should().NotContain("Secret-Title-Value").And.NotContain("Secret-Counter-Value");
        System.Text.Json.JsonDocument.Parse(text).RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant())
            .Should().BeEquivalentTo("topic", "sequence", "streamid", "type");
    }

    [Fact]
    public async Task Module_topics_still_work()
    {
        var caller = await host.CallerHoldingAsync(["read_probe_tickets"]);
        await using var connection = await RealtimeHubTests.ConnectAsync(host, caller.Token);

        var act = () => connection.InvokeAsync("Subscribe", "probe.tickets", Ct);

        await act.Should().NotThrowAsync();
    }
}
