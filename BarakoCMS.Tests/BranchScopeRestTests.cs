using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Realtime.ContentTopicKit;

namespace BarakoCMS.Tests;

/// <summary>
/// A caller who holds branch A cannot reach an entry of a branch scoped type stamped B through the
/// content routes. A hidden entry answers as one that does not exist.
/// </summary>
[Collection("Integration1")]
public class BranchScopeRestTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(string Type, Guid A, Guid B, Guid InA, Guid InB, Caller HoldsA, Caller HoldsBoth);

    private async Task<Setup> ArrangeAsync(bool branchScoped = true)
    {
        var type = await TypeAsync(host, null, branchScoped);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var both = await WriterAsync(host, a, b);
        var inA = await CreateAsync(both.Client, type, a);
        var inB = await CreateAsync(both.Client, type, b);
        var holdsA = await WriterAsync(host, a);
        return new Setup(type, a, b, inA, inB, holdsA, both);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<long> VersionAsync(HttpClient client, Guid id)
    {
        var (status, body) = await SendAsync(client, HttpMethod.Get, $"/api/contents/{id}");
        status.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("version").GetInt64();
    }

    private static async Task<(int Total, List<Guid> Ids)> ListAsync(HttpClient client, string type)
    {
        var (status, body) = await SendAsync(client, HttpMethod.Get, $"/api/contents?contentType={type}&pageSize=50");
        status.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        var ids = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        return (doc.RootElement.GetProperty("totalItems").GetInt32(), ids);
    }

    [Fact]
    public async Task Get_hides_an_entry_of_another_branch_as_not_found()
    {
        var s = await ArrangeAsync();

        (await SendAsync(s.HoldsA.Client, HttpMethod.Get, $"/api/contents/{s.InB}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(s.HoldsA.Client, HttpMethod.Get, $"/api/contents/{s.InA}")).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(s.HoldsBoth.Client, HttpMethod.Get, $"/api/contents/{s.InB}")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_leaves_out_other_branches_and_counts_only_what_is_shown()
    {
        var s = await ArrangeAsync();

        var mine = await ListAsync(s.HoldsA.Client, s.Type);
        mine.Ids.Should().Equal(s.InA);
        mine.Total.Should().Be(1);

        var all = await ListAsync(s.HoldsBoth.Client, s.Type);
        all.Ids.Should().BeEquivalentTo([s.InA, s.InB]);
        all.Total.Should().Be(2);
    }

    [Fact]
    public async Task Put_refuses_an_entry_of_another_branch_as_not_found()
    {
        var s = await ArrangeAsync();
        var body = new { data = new Dictionary<string, object> { ["Title"] = "changed" } };

        (await SendAsync(s.HoldsA.Client, HttpMethod.Put, $"/api/contents/{s.InB}", body)).Status
            .Should().Be(HttpStatusCode.NotFound);

        var version = await VersionAsync(s.HoldsA.Client, s.InA);
        var own = await SendAsync(s.HoldsA.Client, HttpMethod.Put, $"/api/contents/{s.InA}",
            new { data = new Dictionary<string, object> { ["Title"] = "changed" }, version });
        own.Status.Should().Be(HttpStatusCode.OK, own.Body);
    }

    [Fact]
    public async Task Transition_refuses_an_entry_of_another_branch_as_not_found()
    {
        var s = await ArrangeAsync();

        (await SendAsync(s.HoldsA.Client, HttpMethod.Put, $"/api/contents/{s.InB}/status", new { transition = "Call" }))
            .Status.Should().Be(HttpStatusCode.NotFound);

        var own = await SendAsync(s.HoldsA.Client, HttpMethod.Put, $"/api/contents/{s.InA}/status", new { transition = "Call" });
        own.Status.Should().Be(HttpStatusCode.OK, own.Body);
    }

    [Fact]
    public async Task Batch_refuses_a_transition_of_an_entry_of_another_branch_and_saves_nothing()
    {
        var s = await ArrangeAsync();

        var refused = await SendAsync(s.HoldsA.Client, HttpMethod.Post, "/api/contents/batch", new
        {
            steps = new object[]
            {
                new { op = "transition", id = s.InA, transition = "Call" },
                new { op = "transition", id = s.InB, transition = "Call" },
            },
        });
        refused.Status.Should().Be(HttpStatusCode.NotFound, refused.Body);
        refused.Body.Should().Contain("steps[1]");

        // The first step rolled back with the second.
        using var doc = JsonDocument.Parse((await SendAsync(s.HoldsBoth.Client, HttpMethod.Get, $"/api/contents/{s.InA}")).Body);
        doc.RootElement.GetProperty("lifecycleState").GetString().Should().Be("Waiting");

        var own = await SendAsync(s.HoldsA.Client, HttpMethod.Post, "/api/contents/batch", new
        {
            steps = new object[] { new { op = "transition", id = s.InA, transition = "Call" } },
        });
        own.Status.Should().Be(HttpStatusCode.OK, own.Body);
    }

    [Fact]
    public async Task A_type_that_is_not_branch_scoped_is_not_filtered()
    {
        var s = await ArrangeAsync(branchScoped: false);

        (await SendAsync(s.HoldsA.Client, HttpMethod.Get, $"/api/contents/{s.InB}")).Status.Should().Be(HttpStatusCode.OK);
        var listed = await ListAsync(s.HoldsA.Client, s.Type);
        listed.Total.Should().Be(2);
        (await SendAsync(s.HoldsA.Client, HttpMethod.Put, $"/api/contents/{s.InB}/status", new { transition = "Call" }))
            .Status.Should().Be(HttpStatusCode.OK);
    }
}
