using System.Net;
using System.Text.Json;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using static BarakoCMS.Tests.Realtime.ContentTopicKit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// A windowed count of a branch scoped type counts only the caller's branches, as the list does.
/// </summary>
[Collection("Integration1")]
public class ContentCountsWindowBranchTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> CountsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/contents/counts?{query}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    [Fact]
    public async Task A_caller_holding_one_branch_counts_only_that_branchs_entries_in_the_window()
    {
        var type = await TypeAsync(host, null, true);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var both = await WriterAsync(host, a, b);
        await CreateAsync(both.Client, type, a);
        await CreateAsync(both.Client, type, b);
        await CreateAsync(both.Client, type, b);
        var holdsA = await WriterAsync(host, a);
        var query = $"contentType={type}&range=24h&series=hour";

        var mine = await CountsAsync(holdsA.Client, query);
        var all = await CountsAsync(both.Client, query);

        mine.GetProperty("total").GetInt32().Should().Be(1, "the two entries of branch B are not reachable");
        mine.GetProperty("series").EnumerateArray().Sum(x => x.GetProperty("total").GetInt32()).Should().Be(1);
        all.GetProperty("total").GetInt32().Should().Be(3);
        all.GetProperty("series").GetArrayLength().Should().Be(24);
    }
}
